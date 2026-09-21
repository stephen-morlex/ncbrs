# NCBRS load & soak driver (WS-A7)

Fires **concurrent sync batches** at the central tier and reports throughput and
latency percentiles. WS-A7 is about *burst shape, not average*: the load that
matters is what happens when a region's connectivity returns and many village
posts upload their weeks-long outboxes at once. So this drives whole batches in
parallel and reports p50/p90/p95/p99/max, because a mean hides the tail that
falls over under a thundering herd.

It drives the **real** endpoint (`POST /api/sync/batches`) with the real wire
DTOs (referenced from `NCBRS.Contracts`), authenticating as a provisioned
registrar and uploading for an enrolled device — the same path a device takes.

## What it does, and what it deliberately avoids

- Each record carries a **synthetic BRN in a high range** (`9_000_000_000+`) that
  no granted block covers, so records persist as *unconfirmed* registrations (a
  real write down the full path) without drawing down a facility's BRN block or
  colliding with anything already on file.
- Each batch gets a **fresh `X-Transaction-Id`**, so the idempotency layer treats
  it as new work rather than replaying one stored answer.
- A **warmup** phase (not measured) primes JIT, connection pool and caches before
  the measured phase.

## Run it against the dev stack

Bring up the dev stack (`docker compose up -d postgres keycloak kafka`, then the
API, or the SQLite default), then:

```bash
dotnet run --project tools/NCBRS.LoadTest
```

No arguments needed — the defaults line up with the Development seed and the
imported Keycloak realm (`nurse.lado` → Juba Teaching Hospital, device
`TERMINAL-JUBA-01`). Everything is overridable via `NCBRS_LOAD_*` environment
variables:

| Variable | Default | Meaning |
|---|---|---|
| `NCBRS_LOAD_API_BASE` | `http://localhost:5259/` | Central-tier base URL |
| `NCBRS_LOAD_TOKEN_URL` | Keycloak `ncbrs` realm token endpoint | OIDC token endpoint |
| `NCBRS_LOAD_CLIENT_ID` | `ncbrs-device` | Public client with direct-access grants |
| `NCBRS_LOAD_USERNAME` / `_PASSWORD` | `nurse.lado` / `password` | A provisioned registrar |
| `NCBRS_LOAD_FACILITY_ID` | Juba Teaching Hospital | Facility the registrar may act for |
| `NCBRS_LOAD_DEVICE_ID` | `TERMINAL-JUBA-01` | A device **enrolled to that facility** |
| `NCBRS_LOAD_CONCURRENCY` | `16` | In-flight batches (set `= BATCHES` for a pure burst) |
| `NCBRS_LOAD_BATCH_SIZE` | `25` | Records per batch |
| `NCBRS_LOAD_BATCHES` | `200` | Measured batches |
| `NCBRS_LOAD_WARMUP` | `20` | Unmeasured warmup batches |
| `NCBRS_LOAD_BRN_BASE` | `9000000000` | First synthetic BRN |

Exit code is non-zero if any batch failed.

## This is a tool, not the A7 number

Pointed at the dev box it measures **SQLite**, which serialises writes — a floor,
not the figure. The number that goes to a Steering Committee is *this same tool*
against **Postgres at projected national volume** (`Database__Provider=Postgres`,
a facility/device/registrar provisioned there, and `BATCHES`/`BATCH_SIZE`/
`CONCURRENCY` sized to the burst a real region produces). The A6 restore drill's
RTO must be re-measured at that volume too — see
`NCBRS-Business-and-Delivery-Plan.md` (§A6, §A7).

`RequireSignature` is off in dev, so the driver sends no device signature. To
load-test with signatures on, the batch body must be signed byte-for-byte (see
`client/NCBRS.Client.Core`'s `DeviceSigner`) — not yet wired here.
