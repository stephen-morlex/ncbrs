# NCBRS Tier-1 facility/village client (WS-B)

The offline-first client health workers use at the point of registration. It is
the programme's critical path: the entire Tier-1 experience, none of which
existed before this.

## B1 — platform decision: .NET MAUI (not PWA)

Recorded per WS-B1. **MAUI**, for three reasons the plan calls out:

- **Printing is a hard requirement.** A family leaves with a provisional
  certificate carrying a QR code (B7); reliable local/Bluetooth thermal
  printing is a first-class native capability and a persistent weakness in a
  PWA.
- **Encrypted local storage** (B2) — an at-rest-encrypted SQLite database keyed
  to the device — is straightforward natively and awkward-to-impossible to
  guarantee in a browser sandbox.
- **Weeks-offline operation** with a cached credential bundle and BRN block is a
  native app's normal mode, not a PWA's.

MAUI also lets the client reference `NCBRS.Contracts` directly, so the vital
event models and the canonical certificate-verification form are the *same code*
the centre uses rather than a reimplementation that could drift.

## Layout

- **`NCBRS.Client.Core`** — the offline-first *logic*, UI-free and
  dependency-light (only `NCBRS.Contracts`, itself BCL-only). It builds and is
  unit-tested on any host, including CI, which has no mobile tooling. It holds:
  - **B5** — `DeviceBrnAllocator`: device-side BRN block consumption, the
    low-block warning, and the `PROV-` provisional fallback when a block runs
    dry offline.
  - *(coming)* B6 local outbox + sync-batch builder, B8 offline certificate
    verification + bundle refresh, B9 device-key signing.
- **`NCBRS.Client.Core.Tests`** — xUnit tests for the above.

The **MAUI application shell** (the guided registration form B4, certificate
printing B7, PIN unlock B3) is a separate concern that needs device tooling and
field research with midwives/CHWs; it wraps this core and carries no
registration logic of its own, so nothing on the device can diverge from the
centre. It is intended to live in its own repository/project once that tooling
and the pilot are in place.
