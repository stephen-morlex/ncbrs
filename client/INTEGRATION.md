# WS-B client integration guide

How the MAUI shell wires up `NCBRS.Client.Core`. The core is the offline-first
**logic**, complete and unit-tested; the shell adds three things it cannot: the
screens (B4), the at-rest-encrypted local store (B2), and printing (B7). The
shell holds **no registration logic of its own** — everything below is in the
core, so nothing on the device can diverge from what the centre does.

## Components

| Concern | Type | Namespace |
|---|---|---|
| Device identity + batch signing (B9) | `DeviceSigner` | `NCBRS.Client.Sync` |
| Offline PIN unlock, rate-limited (B3) | `OfflinePinLock` | `NCBRS.Client.Auth` |
| BRN block consumption + `PROV-` fallback (B5) | `DeviceBrnAllocator` | `NCBRS.Client.Brn` |
| Local outbox + batch/settlement (B6) | `SyncOutbox` | `NCBRS.Client.Sync` |
| Offline certificate verification (B8) | `CachedVerificationBundle` | `NCBRS.Client.Certificates` |
| Signed offline transfer file (H2) | `OfflineTransferFile` | `NCBRS.Client.Sync` |
| The workflow that composes them | `FacilityClient` | `NCBRS.Client` |
| Every call to the centre (or a District node) | `CentralClient` | `NCBRS.Client.Network` |
| One connectivity window, in the right order | `ConnectivityWindow` | `NCBRS.Client.Network` |

## The network layer

`CentralClient` speaks the centre's contract exactly: the `{ meta, data }`
envelope, a transaction id on every write, and the device signature over the
exact bytes sent on every write that names the device. Every call returns a
`CentralResult` rather than throwing, because on a village link "it did not
work" is the normal case and has different remedies:

| `CentralOutcome` | Means | The shell does |
|---|---|---|
| `Succeeded` | The centre's answer is in `Value` | Nothing more; `ConnectivityWindow` settles it |
| `Held` | A District node has the batch; the centre has not seen it | **Do not tell the family the registration is confirmed.** Keep the upload; the next window resends it and gets the centre's answer |
| `InProgress` | The same transaction is being processed | Resend the same upload after `RetryAfter` |
| `Refused` | A reason retrying will not change (`Errors`) | Show it; the records stay queued |
| `Unauthorized` | No sign-in, or the centre did not accept it | Ask the registrar to sign in while there is connectivity |
| `Unreachable` | No answer, or the centre cannot right now | Try next window |

`CentralEndpoints(Centre, SyncVia)` sends uploads through a District node when
`SyncVia` is set; enrolment, BRN blocks and the bundle always go to the centre,
since a District node carries sync batches and nothing else.

## Signing in: an offline token (decided)

A registrar signs in **once** on the tablet and the device keeps a Keycloak
**offline token** (`OfflineTokenSession`, `NCBRS.Client.Auth`), so a post out
of contact for weeks can still sync when it returns. Its `AccessToken` is what
`CentralClient` takes.

1. **Sign in (online, once):** `var pkce = PkceChallenge.Create();` open
   `session.AuthorizationUrl(pkce, redirectUri)` in the system browser (never
   an embedded web view); when the app's redirect comes back with `code` and
   `state`, check `state == pkce.State` and call
   `session.RedeemAsync(code, pkce, redirectUri)`. An account that cannot hold
   an offline token (anyone but a registrar or community health worker) is
   refused here with a reason, rather than signed in on a session that would
   stop syncing within the hour.
2. **Persist the offline token** (encrypted, B2) in the callback the session
   calls. Keycloak **rotates it on every use**, so the callback fires at each
   renewal and must save the new one; a device keeping the old one ends up
   holding a dead sign-in.
3. **On each app start**, construct the session from the saved token. Access
   tokens are fetched as needed.
4. **When the network layer answers `Unauthorized`**, the sign-in has ended —
   60 days unused, 180 days in all, or revoked by the district. Ask the
   registrar to sign in again while there is connectivity; nothing queued is
   lost. Keycloak being unreachable is reported as `Unreachable`, not as
   "sign in again".
5. **Sign out** with `SignOutAsync()`, which revokes the token at Keycloak, not
   just on the device.

A lost tablet needs its device revoked in NCBRS **and** the registrar's offline
session revoked in Keycloak (RUNBOOK, "a tablet is lost or stolen"): the device
key stops writes, but the token alone still reads until it is revoked.

**Until this layer, no upload the device built could have been accepted.**
`FacilityClient` serialised the bare batch; the centre binds
`ApiRequest<SyncBatchRequest>` and refused it with "data is required", and a
District node refused it for lacking a transaction id. The TLS rehearsal now
runs the device path in Production and fails if that shape comes back.

The signature format, provisional-identifier format, certificate verifier and
vital-event models all come from `NCBRS.Contracts`, shared with the server.

## Lifecycle

1. **Enrol (online, once).** `DeviceSigner.Generate()`; send `PublicKeyPem` to
   the centre's enrolment endpoint. Persist the private key in the encrypted
   store. Never send the private key anywhere.
2. **Provision (online).** Fetch and persist: the granted BRN block
   (`start`/`end`), the offline signing bundle (`/offline-bundle`) and the
   revocation lists, and the registrar PIN credential
   (`OfflinePinLock.CreateCredential(pin)`).
3. **Unlock (offline).** Build `OfflinePinLock` from the persisted credential +
   rate-limit state; `Unlock(pin, now)`. Persist `FailedAttempts` /
   `LockedUntilUtc` after **every** attempt.
4. **Register (offline).** Construct a `FacilityClient` for the unlocked
   session and call `RegisterBirth(...)`. Persist the allocator cursor and the
   outbox after each call. Surface `RegistrationDraft.BlockLow` so the slip is
   shown, and `IsProvisional` so a provisional slip is marked as such.
4b. **Top up the block (online).** When `NeedsMoreNumbers` is true, POST
   `request-brn-block` — naming this device, and signed like any other write
   (below) — and pass the granted range to `GrantNextBlock(start, end)`;
   persist the staged block. The allocator rolls over to it when the current
   block runs dry, so a device that tops up in time never issues a `PROV-`
   identifier. The fallback still fires if no window came in time.
5. **Sync (online).** Call `ConnectivityWindow.RunAsync(state, now)` each
   time there is connectivity. It finishes the upload already in flight
   (`ClientSyncState.InFlight`), uploads what was registered since, settles
   both through `Settle(...)`, tops up the BRN block if low and refreshes the
   verification bundle if due — calling your `persist` callback after each
   change, and **before** an upload is sent, so a window cut short leaves
   nothing the next one cannot finish. Persist the outbox and allocator there
   too. Rejected records stay queued; `AssignedBrn` on a settled provisional
   record replaces the number the family is holding. Driving `CentralClient`
   directly instead: persist the `SignedUpload` before sending it, and retry
   **that** upload — the same bytes under the same transaction id — until it
   is settled; never rebuild it.
6. **Transfer (no network at all).** `BuildTransferFile()` → write to removable
   media; a sync point opens it with `OfflineTransferFile.Open(bytes, publicKey)`
   and forwards the body if accepted (H2).
7. **Verify a certificate (offline).** `CachedVerificationBundle.Verify(qr, now)`.
   Check `RefreshDue(now)` each connectivity window and refetch the bundle
   before it goes stale, or it will correctly but uselessly answer Unknown.

## Every online write is signed

Not only sync. Registration, correction, BRN block requests, certificate issue
and reprint, the maternal questionnaire and both outcomes all name a device,
and the centre holds each to the same proof: the `deviceId` in the body must be
this enrolled, active device at the record's facility, and the request must
carry `DeviceSignature.HeaderName` with `DeviceSigner.Sign(bytes)` over **the
exact bytes sent**. Serialise once, sign those bytes, send those bytes —
re-serialising after signing changes whitespace or property order and the
signature fails. A device may never send `ncbrs-web`; that is the management
site's channel, and the centre refuses it from any other client.

## What the shell must persist (B2, encrypted)

The core is a set of pure state machines; it does no I/O. After each operation
the shell saves the state so it survives offline across restarts:

- Device private key (once).
- BRN allocator: `NextAvailable`, `ProvisionalSequence`, and any staged
  `PendingBlockStart` / `PendingBlockEnd`.
- Outbox: `Pending`.
- PIN lock: `FailedAttempts`, `LockedUntilUtc`.
- Offline bundle: the signing keys + revocation lists + fetched-at time.

Losing the allocator cursor re-hands printed numbers; losing the PIN state
resets the brute-force limit — so these saves are not optional.

## What is not in the core

`FacilityClient` is what the shell drives. The shell still owns the encrypted
store (B2), the guided registration form designed with midwives/CHWs (B4), and
certificate printing with a QR (B7) — none of which is registration logic, and
all of which need a device environment.
