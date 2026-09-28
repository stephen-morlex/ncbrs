# WS-B client integration guide

How the MAUI shell wires up `NCBRS.Client.Core`. The core is the offline-first
**logic**, complete and unit-tested, and it now includes the at-rest-encrypted
store (B2). The shell adds what it cannot: the screens (B4), the platform's key
store, and printing (B7). The
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
| The encrypted store, and the session rebuilt from it (B2) | `EncryptedStateFile`, `DeviceState`, `DeviceSession`, `StateKey` | `NCBRS.Client.Storage` |
| The encrypted store, and the session rebuilt from it (B2) | `EncryptedStateFile`, `DeviceState`, `DeviceSession`, `StateKey` | `NCBRS.Client.Storage` |

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

1. **Hand over (online, once): the district officer enrols the tablet.**
   Enrolling is an officer's act, since a device enrolled by whoever holds it
   would close no hole, and an officer cannot hold an offline token. So the
   officer signs in on the tablet with `InteractiveSignIn`, an ordinary
   code+PKCE session, and picks the facility from `ListFacilitiesAsync` (the
   centre scopes the list to the officer's county). They then enrol the
   device's key with `EnrolDeviceAsync` and **end the session**
   (`EndAsync`, which revokes it at Keycloak), so it does not stay behind at the
   post. `DeviceSigner.Generate()` runs first and its key is saved
   (`ExportPrivateKeyPem()`) before enrolling; the private key is never sent.
   Every sign-in URL carries `prompt=login`. The tablet's browser is shared,
   and without it the registrar would be signed straight in as the officer.
2. **Provision (online).** The registrar signs in (offline token, above),
   draws the first BRN block, and fetches the verification bundle. Then the
   **staff PINs**:
   - A registrar sets their own PIN **at the centre**
     (`SetOwnPinAsync`; the centre holds the policy and answers with its reason).
   - The device fetches every registrar's credential for its facility
     (`FetchStaffCredentialsAsync`, audited at the centre against the device)
     and keeps them with `StaffUnlock.Provision`.
   - A hash the device cannot verify is left out, never trusted.
   - Refetch each connectivity window, so new staff can unlock and departed
     staff cannot.
3. **Unlock (offline).** The registrar picks their name and enters their PIN:
   `StaffUnlock.Attempt(state, registrarId, pin, now)`. Save the state after
   **every** attempt. **The wrong-guess counter is the device's, not each
   person's**, or a thief would get five guesses per name on the list. The
   centre's hash and the tablet's lock agree exactly: `DevicePinCompatibilityTests`
   pins the real code on both sides, and the harness proves it live.
4. **Register (offline).** Construct a `FacilityClient` for the unlocked
   session and call `RegisterBirth(..., registeredByRegistrarId)` with the
   registrar who unlocked, so the birth is credited to them. Persist the allocator cursor and the
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

## The encrypted store (B2, built)

The core is a set of pure state machines; it does no I/O. `NCBRS.Client.Storage`
is where their state goes, so the shell does not have to decide what to save:

- **`DeviceState`** is everything the tablet must still know after a restart:
  identity, device key, the allocator's whole state, the outbox, the upload in
  flight, the verification bundle, the offline token, the staff PIN
  credentials, and the device's wrong-guess count.
- **`DeviceSession.Restore(state)`** rebuilds `FacilityClient` and
  `ClientSyncState` from it. **`session.Capture(state)`** writes them back,
  leaving the token and PIN (which the session does not own) alone. Save after
  **every** act, and before showing its result: a number on a slip the store
  does not know about is one the next registration can hand out again.
- **`EncryptedStateFile`** is AES-256-GCM, with a fresh nonce on every save. It
  writes a temporary file, flushes it and renames it over the old one, so a
  battery dying mid-save keeps the previous state whole. **A file it cannot
  read throws `StateFileUnreadableException`, never "no state".** Read as
  empty, the app would start as a fresh device and its first save would
  overwrite births nobody has synced. Stop and send the tablet to the district.
- **`StateKey.ResolveAsync`** gets the key from the platform key store
  (SecureStorage, Android Keystore). It creates one **only when no store
  exists yet**. A missing key beside an existing store is refused for the same
  reason.

Two things the obvious approach gets wrong, both pinned by tests:

- **The allocator's `BlockStart` and `BlockEnd` are part of its state,** not
  just the cursor. Rolling over to a staged block replaces the current one, so a
  store that kept only `NextAvailable` restores it against the old range.
- **`DeviceSigner.ExportPrivateKeyPem()`** is the only way to persist a
  generated key; without it a restarted tablet would need enrolling again.

Losing the allocator cursor re-hands printed numbers; losing the PIN state
resets the brute-force limit — so these saves are not optional.
## What is not in the core

`FacilityClient` is what the shell drives. The shell owns only the platform's
places for the store (the app data directory and SecureStorage, in
`NCBRS.Client.App/DeviceStorage.cs`), the guided registration form designed
with midwives/CHWs (B4), and certificate printing with a QR (B7) — none of
which is registration logic.
