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
| `NCBRS.Api` | 5259 | `/health` (anonymous, 200 when up; alert on `status` = `degraded`: outbox not draining, or WAL archiving off/failing) | HTTP only; stages events in the outbox |
| `NCBRS.Consumer` | 5281 | `/health` (200) | Read model + dashboards + DHIS2 export |
| `NCBRS.Relay` | — (worker) | process running | Drains the outbox to Kafka |
| Keycloak | 8080 | `/realms/ncbrs` | Auth; realm imported at container start |
| Postgres | 5433 | `pg_isready` | Central system of record |
| Kafka | — | broker up | Event backbone |

## Incident: registrations succeed but the dashboard does not update

**Most common report.** The register API is fine (births are landing in
Postgres); the reporting pipeline behind it has stalled.

1. **Is the outbox draining?** The Api's `/health` answers directly: `outbox`
   has the pending count and the oldest pending event, and `status` becomes
   `degraded` once that is older than `OperationalHealth:OutboxStaleAfter`
   (default 2 minutes; the Relay polls every 5 s). By hand: `SELECT count(*) FROM "OutboxMessages" WHERE
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

- **The only signal is `pg_stat_archiver`**, and monitoring must alert on
  it. The Api's anonymous `/health` reports it as `walArchive`, with
  `failingNow: true` when the latest attempt failed. It also reports
  `enabled: false` when archiving is off, which is the same danger with
  nothing even failing. In both cases `status` is `degraded` and `problems`
  says why. By hand: `SELECT failed_count, last_failed_time, last_failed_wal FROM
  pg_stat_archiver;`. `failed_count` is cumulative, so on its own it keeps
  "alerting" long after archiving has recovered. `failingNow` compares the
  last failure with the last success instead.
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

## Incident: a service refuses to start (HTTPS, Kafka TLS)

Outside Development:

- The **Api** and **Consumer** refuse `Keycloak:RequireHttpsMetadata=false`.
  Serve Keycloak over HTTPS and remove the setting. Do not set
  `ASPNETCORE_ENVIRONMENT=Development` to get past it — that also turns on
  dev-only migration on startup, seeding and the throwaway signing key.
- The **District node** refuses an `http://` `Central:BaseUrl` or
  `Central:TokenEndpoint`. Both must be `https://`: the node sends its
  service-account password to one and batches of birth records to the other.
- The **Relay** and **Consumer** refuse a Kafka link that is not both
  encrypted and authenticated. Set `Kafka__SecurityProtocol=SaslSsl` (the
  default) with `Kafka__SaslUsername` and `Kafka__SaslPassword` from the secret
  store (`Kafka__SaslMechanism` defaults to `ScramSha512`), or `Ssl` with
  `Kafka__SslCertificateLocation` and `Kafka__SslKeyLocation` for mutual TLS.
  `Kafka__SslCaLocation` names the broker's CA when it is not in the system
  store.
- A Relay that **starts but delivers nothing** with `SSL handshake failed` or
  an authentication error in its log has the wrong protocol, CA or credentials
  for the broker. This is logged as an error and will not fix itself on retry;
  the outbox backlog on the Api's `/health` grows meanwhile.

These relaxations belong only in `appsettings.Development.json`. Device batch
signatures (`DeviceEnrolment:RequireSignature`) are on everywhere except where
Development turns them off.

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
- **How often a node retries** is its own configuration:
  `Forwarder:InitialBackoff` (default 30 s), doubling on each failed attempt up
  to `Forwarder:MaxBackoff` (default 15 min). A district whose link is up for
  an hour a day may want a longer start and a shorter ceiling. Before
  2026-09-27 these settings were read by nothing and the defaults always
  applied, so a node configured earlier may have been retrying on a schedule
  nobody chose.
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

## Deployment: county groups in Keycloak (reporting scope)

Reporting scopes a district officer to their county by a **Keycloak group**,
because the Consumer cannot read the registry. Every realm needs:

1. A group per county the realm's officers work in, **named by p-code**
   under `/counties`: `/counties/SS0101` for Juba. Use the code the registry
   uses; `Facility.CountyCode` is the one to match.
2. A **group-membership mapper** on the web and device clients: claim
   `groups`, **full group path on**, added to the access token. The dev realm
   (`keycloak/ncbrs-realm.json`, mapper `ncbrs-county-groups`) is the
   reference.
3. **Each district officer in exactly one county group**, the county of the
   facility they are registered at.

What goes wrong, and what it looks like:

- **An officer in no group:** the dashboard answers 403, "Your account has no
  county in the identity provider". Add them to their county's group.
- **An officer whose group and facility disagree:** *every* Api call answers
  403, "Your county is recorded inconsistently". Correct the group, or the
  registrar's facility, whichever is wrong. This check exists so a moved
  officer cannot act on one county while reading another's figures.
- **After changing the realm file in dev:** Keycloak imports the realm only
  when it doesn't exist yet, so recreate the container to pick up changes:
  `docker compose up -d --force-recreate keycloak`. This discards anything
  changed by hand in the dev realm.

## Deployment: DHIS2 export configuration

The shipped base settings configure no DHIS2 instance, on purpose: its data
element UIDs and org units belong to the Ministry's DHIS2 and differ between
instances. Before the export goes anywhere, set in the Consumer's environment
or secret store:

- `Dhis2Export__LiveBirths`, `__LiveBirthsMale`, `__LiveBirthsFemale`,
  `__FetalDeaths`, `__NeonatalDeaths`, `__MaternalDeaths`,
  `__RegisteredWithinWindow`: the instance's data element UIDs. One left unset
  is skipped, not guessed.
- `Dhis2Export__OrgUnits__<county p-code>`: the org unit for each county, keyed
  by the **p-code** (`Dhis2Export__OrgUnits__SS0101` for Juba), which is what
  the export groups by.

**A county missing from the map** appears in the export's `unmapped` list and
its births are not sent. Check that list after every configuration change: an
empty export with a long `unmapped` list is a map keyed wrongly, not a quiet
month.

## Deployment: database privileges (after every migration)

Two roles, never one:

- **The owner** owns the tables and runs migrations (`dotnet ef database
  update`). Its credentials belong to the deployment, not to any running
  service.
- **The application role** (e.g. `ncbrs_app`) is what the Api, Relay and
  Consumer connect as, via `ConnectionStrings__Default`.

After **every** migration, as the owner:

```
psql -v app_role=ncbrs_app -f deploy/postgres/app-role-grants.sql
```

This grants the application role the whole register and revokes `UPDATE`,
`DELETE` and `TRUNCATE` on `AuditLogs`. It is idempotent. If you skip it after
a migration, the new table has no grant and the first request that touches it
fails with `permission denied`. That is deliberate: loud during the deployment
is better than silently over-granted.

**Never run a service as the owner.** The owner can drop the append-only
trigger; the application role cannot, and the script's `REVOKE` means it
cannot rewrite the trail even with the trigger gone. To confirm the grants on a
live database:

```
SELECT has_table_privilege('ncbrs_app', '"AuditLogs"', 'UPDATE'),
       has_table_privilege('ncbrs_app', '"AuditLogs"', 'DELETE');   -- both false
```

## Escalation

- Registration path down (API 5xx on register) → highest priority; the offline
  tier buffers, but connected facilities cannot register. Check Api + Postgres.
- Reporting behind → not urgent; derived and rebuildable. Fix the Relay/Consumer
  and let it catch up.
- Suspected data integrity (an audit row disputed) → the trail is append-only at
  the database; a row cannot have been rewritten through the app. Preserve the
  WAL archive and escalate to the DPO.
