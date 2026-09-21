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
   `request-brn-block` and pass the granted range to `GrantNextBlock(start, end)`;
   persist the staged block. The allocator rolls over to it when the current
   block runs dry, so a device that tops up in time never issues a `PROV-`
   identifier. The fallback still fires if no window came in time.
5. **Sync (online).** `BuildSignedUpload()` → POST `Body` **verbatim** with the
   `HeaderName` header. Feed the `SyncBatchResponse` to `Settle(...)`; persist
   the outbox. Rejected records stay queued; `AssignedBrn` on a settled
   provisional record replaces the number the family is holding.
6. **Transfer (no network at all).** `BuildTransferFile()` → write to removable
   media; a sync point opens it with `OfflineTransferFile.Open(bytes, publicKey)`
   and forwards the body if accepted (H2).
7. **Verify a certificate (offline).** `CachedVerificationBundle.Verify(qr, now)`.
   Check `RefreshDue(now)` each connectivity window and refetch the bundle
   before it goes stale, or it will correctly but uselessly answer Unknown.

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
