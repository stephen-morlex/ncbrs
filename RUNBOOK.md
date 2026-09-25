# NCBRS operations runbook

First response for the central tier. Every procedure here rests on one fact:
**Postgres is the system of record.** The Kafka stream, the read model, the
dashboards and the exports are all derived from it and can be rebuilt; none of
them can lose a registration. So the reflex for most reporting-side incidents is
*rebuild the derived thing*, never *edit the register*.

For how the pieces fit, see `ARCHITECTURE.md`; for why each behaves as it does,
`CLAUDE.md`.

## Services and health

| Service | Dev port | Health | Notes |
|---|---|---|---|
| `NCBRS.Api` | 5259 | `/health` (401 = up, auth required) | HTTP only; stages events in the outbox |
| `NCBRS.Consumer` | 5281 | `/health` (200) | Read model + dashboards + DHIS2 export |
| `NCBRS.Relay` | — (worker) | process running | Drains the outbox to Kafka |
| Keycloak | 8080 | `/realms/ncbrs` | Auth; realm imported at container start |
| Postgres | 5433 | `pg_isready` | Central system of record |
| Kafka | — | broker up | Event backbone |

## Incident: registrations succeed but the dashboard does not update

**Most common report.** The register API is fine (births are landing in
Postgres); the reporting pipeline behind it has stalled.

1. **Is the outbox draining?** `SELECT count(*) FROM "OutboxMessages" WHERE
   "DispatchedAtUtc" IS NULL;` growing and not falling → the Relay is not
   publishing. Check the Relay process is running; restart it. It leases rows,
   so a restart is safe and it resumes where it left off. Nothing is lost while
   it is down — the events sit in the outbox.
2. **Is the Consumer running and keeping up?** Check `/health` on 5281 and
   Kafka consumer-group lag for `ncbrs-dashboard-updater`. If the process is
   down, restart it; it commits offsets **after** projecting, so a crash
   reprocesses rather than skips.
3. **Do not** conclude births are lost because the dashboard is behind. Confirm
   against Postgres (`SELECT count(*) FROM "BirthRecords"`), which is the truth.

## Incident: the Consumer won't start, or the dashboard 500s after a deploy

Likely a **read-model schema mismatch**: the read model is created with
`EnsureCreated` (a no-op against an existing store), so a column added or
renamed never appears in a store built before the change, and
`ReadModelSchema.EnsureUsable` refuses to start rather than fail later on a
query.

**Remedy (always safe):** stop the Consumer, delete its read-model store
(`ncbrs-readmodel.db` in dev; the reporting replica in production), restart. It
rebuilds from Kafka. To repopulate historical data, reset the consumer group to
the earliest offset before restarting. Nothing is lost — the projection is the
system of record for nothing.

## Incident: WAL archiving is failing (silent until you need it)

Archiving fails **silently** — the database keeps accepting registrations and
the absence of any recovery point is discovered on the day it is needed.

- **The only signal is `pg_stat_archiver.failed_count`** — monitoring must
  alert on it. `SELECT failed_count, last_failed_time, last_failed_wal FROM
  pg_stat_archiver;`.
- Known cause seen here: a named archive volume mounts root-owned while postgres
  runs as uid 999 → every archive attempt fails. The compose service chowns the
  archive directory; verify permissions if `failed_count` climbs.
- The archive is on its own volume, not beside the data. Recovery objectives
  and the restore procedure are in `NCBRS-Business-and-Delivery-Plan.md` (§A6).
  A restore is only a recovery if the append-only audit triggers come back
  enforcing — the drill checks this, not just that rows returned.

## Incident: offline certificate verification answers "Unknown" for everything

A device's cached bundle has gone **stale** (past its `NextUpdateUtc`) or was
never fetched. This is correct behaviour — a device that cannot confirm a
certificate must not say "valid" — but it is useless until refreshed. The device
must refetch `/offline-bundle` and the revocation list on its next connectivity
window (the client's `CachedVerificationBundle.RefreshDue` signals when). A list
signed by a key the device does not hold reads as `Untrusted`; refreshing the
bundle (which carries the whole key set) resolves it.

## Incident: the Api refuses to start (certificate signing)

Outside Development, signing refuses to start without
`CertificateSigning:PfxPath`. Provide the real key (secret store / HSM). The dev
fallback mints a throwaway key whose certificates stop verifying on restart, so
never rely on it outside dev.

## Incident: a facility has gone quiet

A silent device is indistinguishable from a district with no births — only one
needs intervention. `GET /api/devices/alerts` is the district's queue;
`DeviceSilenceMonitor` raises `Silent` (a device that reported and stopped — a
link or a battery) vs `NeverReported` (a deployment that failed at handover)
against the facility's connectivity profile, so an offline-first post is not
flagged for a normal fortnight offline. Acknowledging is not resolving: only the
device reporting again clears it. There is no delivery channel — the queue is read.

## Incident: a device's batch was rejected, or the district node is holding it

- **202 vs 200:** *queued* at the district node is `202`; *forwarded and
  answered by the centre* is `200`. A family must not be told a registration is
  confirmed on a `202`.
- **"The centre said no" ≠ "the centre did not answer":** only a 4xx (except
  408/429, and a 409 carrying `Retry-After`, which means "already in progress")
  stops the node retrying; everything else stays queued. A dropped link never
  discards a birth.
- **`slowToFinish` in the node's `/api/Sync/status` is not an outage.** Those
  batches reached the centre and ran out of time before it finished. The link
  is up, so don't send anyone to check it. Each retry allows twice as long
  (`Central:Timeout` + `Central:TimeoutPerRecord` × records, capped at
  `Central:MaxTimeout`). If the count stays up, the centre is slow: look at the
  central tier's load.
- **Every forwarded batch `Rejected` with 403?** Check the centre's audit for
  `DeviceRefused:SignatureFailed`. The node forwards the device's signature
  header untouched, so a failure there means the device signed different bytes
  than it sent, or signed with a key that is not the one enrolled.
- **Partial rejection:** the device keeps exactly the rejected records queued
  and re-sends only those; a re-uploaded batch is idempotent by transaction id
  (one batch, no duplicates).

## Reassurance: "did a replay double-count the district's births?"

No. The reporting projection is idempotent **by construction** — facts are keyed
by BRN, never counters, so the same event applied twice says what it said once.
Replaying a partition, or rebuilding from offset 0, leaves totals unchanged;
out-of-order arrivals are held and applied in occurrence order. Replay is a
supported operation, not a fault.

## Escalation

- Registration path down (API 5xx on register) → highest priority; the offline
  tier buffers, but connected facilities cannot register. Check Api + Postgres.
- Reporting behind → not urgent; derived and rebuildable. Fix the Relay/Consumer
  and let it catch up.
- Suspected data integrity (an audit row disputed) → the trail is append-only at
  the database; a row cannot have been rewritten through the app. Preserve the
  WAL archive and escalate to the DPO.
