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
- It spreads the burst across a **fleet of distinct devices** (`DEVICES`, default
  32), enrolled fresh per run at the target facility (public-key only). A real
  burst is many *posts* uploading at once; pinning every concurrent batch to one
  device instead serialises them all on that one `Device` row's last-seen update —
  measuring row-lock contention, not the system. Set `DEVICES=1` to use the single
  seeded device and see that ceiling for yourself. Fleet mode needs a
  **`CanEnrolDevices`** identity (district officer / ministry admin).
- A **warmup** phase (not measured) primes JIT, connection pool and caches before
  the measured phase.

## Run it against the dev stack

Bring up the dev stack (`docker compose up -d postgres keycloak kafka`, then the
API, or the SQLite default), then:

```bash
# Fleet mode (default) enrols devices, so sync as a CanEnrolDevices identity:
NCBRS_LOAD_USERNAME=district.officer dotnet run --project tools/NCBRS.LoadTest
```

The defaults line up with the Development seed and the imported Keycloak realm
(Juba Teaching Hospital). Fleet mode (`DEVICES>1`, the default) enrols its own
devices, so the identity needs `CanEnrolDevices` — `district.officer` in the dev
realm. With `DEVICES=1` any provisioned registrar for the facility works
(`nurse.lado`). Everything is overridable via `NCBRS_LOAD_*` environment
variables:

| Variable | Default | Meaning |
|---|---|---|
| `NCBRS_LOAD_API_BASE` | `http://localhost:5259/` | Central-tier base URL |
| `NCBRS_LOAD_TOKEN_URL` | Keycloak `ncbrs` realm token endpoint | OIDC token endpoint |
| `NCBRS_LOAD_CLIENT_ID` | `ncbrs-device` | Public client with direct-access grants |
| `NCBRS_LOAD_USERNAME` / `_PASSWORD` | `nurse.lado` / `password` | The sync identity. Fleet mode needs `CanEnrolDevices` (e.g. `district.officer`) |
| `NCBRS_LOAD_FACILITY_ID` | Juba Teaching Hospital | Facility the identity may act for |
| `NCBRS_LOAD_DEVICES` | `32` | Devices enrolled per run and round-robined across; `1` uses the single seeded `DEVICE_ID` |
| `NCBRS_LOAD_SIGN` | on in fleet mode | Sign each batch with its device key (see *Device signatures*) |
| `NCBRS_LOAD_DEVICE_ID` | `TERMINAL-JUBA-01` | The single device used when `DEVICES=1` |
| `NCBRS_LOAD_CONCURRENCY` | `16` | In-flight batches (set `= BATCHES` for a pure burst) |
| `NCBRS_LOAD_BATCH_SIZE` | `25` | Records per batch |
| `NCBRS_LOAD_BATCHES` | `200` | Measured batches |
| `NCBRS_LOAD_WARMUP` | `20` | Unmeasured warmup batches |
| `NCBRS_LOAD_BRN_BASE` | `9000000000` | First synthetic BRN |

Exit code is non-zero if any batch failed.

## Measured — dev-hardware Postgres, driver fixed (2026-09-25)

The earlier figures below were inflated by this driver's own names. They
flagged almost every same-day pair as a duplicate (`SyntheticBirths` explains
how), so each run measured a flood of review-queue writes that real
registrations don't produce. Re-measured on the same hardware and the same
day. Each run used its own fresh database, with signing enforced, a 32-device
fleet and batches of 25. The only difference is the driver:

| Run | Old driver | Fixed driver |
|---|---|---|
| Duplicate candidates raised (13,071 records) | 166,961 | **0** from load |
| Concurrency 16, 10,000 records | 131.6 rec/s; p50 3.1 s / p99 4.6 s | **295.2 rec/s**; p50 1.3 s / p99 1.9 s |
| Concurrency 1, 2,500 records | 11.9 rec/s → 84 ms/record | **38.8 rec/s → 26 ms/record** |

`SyntheticBirthsTests` holds the fix: 400 generated names, every pair on the
same date, sex and facility, all under the matcher's review threshold. If you
change how this driver builds a record, keep that test passing, or the numbers
stop meaning anything.

To measure without touching the dev database, create a scratch one in the
compose Postgres (`docker exec ncbrs-postgres-1 createdb -U ncbrs ncbrs_a7`),
point an API at it (Development migrates and seeds it on start), run the
driver, then `dropdb` it.

## Measured — dev-hardware Postgres (2026-09-21), superseded

Against the compose Postgres (`Database__Provider=Postgres`, `district.officer`
syncing for Juba Teaching Hospital), on a developer laptop with **WAL archiving
on** (the A6 setup, which adds commit overhead) and all devices at **one
facility** — so a conservative floor, not production hardware:

| Run | Result |
|---|---|
| Concurrency 1, single batch of 25 | **~6.6 rec/s**, p50 ~3.2s/batch → **~128 ms per record** |
| Concurrency 16, 32-device fleet, 5000 records | **~32 rec/s, 0 failures**, p50 12.2s / p99 16.4s per 25-record batch |
| Concurrency 16, **1** device (`DEVICES=1`) | throughput collapses, ~55/200 batches time out (HTTP 500) — every batch contends on one `Device` row |

Reading: concurrency scales throughput ~5× (1→16), so the write path is not
globally serialised. The **~120 ms per-record floor** is the thing to watch. It is
**not** the duplicate-detection scan — `EXPLAIN ANALYZE` on that query shows it
uses `IX_BirthRecords_DateOfBirth` and runs in ~5 ms even over a dense ±3-day
window. The floor is the **per-record write path**: a `SaveChanges` and savepoint
per record (so one bad row costs only itself), the append-only audit triggers
and the outbox insert. It is not a commit per record: the batch is one
transaction (ADR 0001).
The one duplicate-scan cost that *was* real — the scan change-tracked its ~900
read-only candidates per record, bloating the change tracker across a batch and
inflating the tail — is fixed with `AsNoTracking` (p90/p99 roughly halved,
throughput +24% at concurrency 1). What remains before national volume is the
commit path itself, which is a deliberate per-record-isolation choice, not a bug.

## Still not the national number

This is dev hardware with WAL archiving on and a single facility. The number a
Steering Committee gets is *this same tool* against **production-grade Postgres**,
with the fleet spread across **multiple facilities** and `BATCHES`/`BATCH_SIZE`/
`CONCURRENCY` sized to a real region's reconnect burst. The A6 restore RTO must be
re-measured at that volume too — see `NCBRS-Business-and-Delivery-Plan.md`
(§A6, §A7).

`RequireSignature` was off for the 2026-09-21 runs (dev) and on for the
2026-09-25 re-measurement.

## Device signatures

In fleet mode the driver **signs every batch** with the enrolling device's key,
so load tests run the production path (`DeviceEnrolment:RequireSignature: true`).
Each device's key pair comes from `DeviceSignature.GenerateKeyPair`; only the
public half is enrolled, and the private half never leaves the process. The
batch is serialised **once** and those exact bytes are both signed and sent,
because the centre verifies the raw body byte for byte — re-serialising after
signing would produce a body the signature does not cover.

| `NCBRS_LOAD_SIGN` | Behaviour |
|---|---|
| unset | On in fleet mode (`DEVICES > 1`), off for the single seeded device |
| `true` | Sign every batch. With `DEVICES=1` it refuses to start: the seeded device's private key was discarded at enrolment, exactly as a real device keeps its own |
| `false` | Send unsigned batches |

**A server that does not enforce signatures accepts batches without verifying
them**, so a clean run proves the signing path only against
`RequireSignature: true`. Verified that way (2026-09-24): signed, 12/12 batches
accepted; the same run unsigned against the same server, 12/12 refused with
403 — which is what shows enforcement was actually on.
