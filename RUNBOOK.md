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

Outside Development, the Api refuses to start without
`CertificateSigning:PfxPath` (an ECDSA P-256 key), with a key it cannot read,
or with `CertificateSigning:KeyId` left at the development default `ncbrs-dev`.
Provide the real key (secret store / HSM) and a production-unique key id. The
dev fallback mints a throwaway key whose certificates stop verifying on restart,
so never rely on it outside dev.

Until #125 this was only checked on first use: an Api with no key started,
reported `/health` ok, and failed every certificate operation with a 500 —
including the offline bundle, so facility tablets could not refresh and their
verification went to "Unknown" as their caches expired.

## Incident: a service refuses to start (HTTPS, CORS, database or Kafka TLS)

Outside Development:

- The **Api** and **Consumer** refuse a `Keycloak:Authority` that is not
  `https://`. Set `Keycloak__Authority` to the realm URL exactly as it appears
  in the tokens' `iss` claim.
- The **Api** and **Consumer** refuse `Keycloak:RequireHttpsMetadata=false`.
  Serve Keycloak over HTTPS and remove the setting. Do not set
  `ASPNETCORE_ENVIRONMENT=Development` to get past it — that also turns on
  dev-only migration on startup, seeding and the throwaway signing key.
- The **District node** refuses an `http://` `Central:BaseUrl` or
  `Central:TokenEndpoint`. Both must be `https://`: the node sends its
  service-account password to one and batches of birth records to the other.
- The **Api** and **Relay** refuse a registry that is not Postgres
  (`Database__Provider=Postgres`), and a Postgres connection that does not
  verify the server: add `SSL Mode=VerifyFull` to `ConnectionStrings__Default`,
  with `Root Certificate=<path>` when the CA is not in the system store. A Unix
  socket or loopback host needs neither.
- The **Api** and **Consumer** refuse a `WebClientCors:AllowedOrigins` entry
  that is a wildcard, is not a bare origin (no path, no trailing slash), or is
  not `https://`. Set the management site's exact origin on both services,
  e.g. `WebClientCors__AllowedOrigins__0=https://registry.example`. With no
  entry, no browser on another origin can call the service: every call from the
  site then fails as a CORS error, which means the origin is missing, not that
  the API is down.
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

## Incident: every signed-in request answers 401

If the Api or Consumer logs `Cannot obtain the identity provider's metadata and
signing keys from <authority>`, the service cannot reach Keycloak or cannot
verify it, and **nobody** can sign in to it until that is fixed. It is not a
problem with users' tokens. Check, in order: Keycloak is up; `Keycloak__Authority`
is exactly the realm URL in the tokens' `iss` claim; the host trusts the CA that
signed Keycloak's certificate (on Linux, the system bundle or `SSL_CERT_FILE`).
It is logged once a minute while it lasts. Without that line, a 401 is a
caller's token being wrong, expired or from another realm.

## Incident: certificate checks or health probes answer 429

Requests **without a valid token** are limited per client address
(`UnauthenticatedRateLimit__PermitsPerMinute`, default 120). Signed-in callers
are never limited, so registrations are unaffected.

- The Api logs `Unauthenticated requests from client:<address> exceeded …` once
  a minute per address. **If that address is your reverse proxy or load
  balancer**, every public caller is sharing one allowance: list the proxy in
  `UnauthenticatedRateLimit__TrustedProxies__0=<ip>` (IP addresses only) so the
  real client address from `X-Forwarded-For` is used. The header is ignored for
  any proxy not listed.
- If it is a single outside address, the limit is doing its job.
- A monitor polling `/health` well under twice a second never reaches it.

## Onboarding: a facility, its staff and its tablet

What a pilot district does on its first day, in this order. Each step names who
does it; the registry refuses the step from anyone else.

1. **Create the facility** (Ministry). Facilities → Add a facility: its name,
   tier, connectivity and where it is (a county, or a payam, boma or village
   within one). The registry places it in its county from that, and gives it a
   range of 100,000 registration numbers that no other facility holds. Check
   the county shown in the confirmation: it decides who oversees the facility.
2. **Create each person's Keycloak account** (whoever administers the realm).
   Give it its realm role (`facility-registrar`, `community-health-worker`,
   `district-officer`) and its county group (`/counties/<p-code>`). The
   county group is what puts the account on the right district officer's list
   in step 4, and a district officer cannot be added without one.
3. **The person signs in to the registry website once.** They are told their
   account is waiting to be added. That sign-in is what puts them on the list:
   the account declares itself, so nobody copies an id out of Keycloak.
4. **Add them** (their county's district officer; the Ministry for an officer
   or anyone without a county group). Registrars → Waiting to be added → Add:
   their facility and role. Only roles the account holds in Keycloak are
   offered. They can work at once.
5. **They set their PIN** on the tablet, with signal ("Set or change my PIN"). The
   tablet downloads every PIN for its facility at each sync.
6. **Hand the tablet over** at the facility (a district officer, on the tablet).
   The tablet checks its enrolment against the registry before it counts.
7. **Pair and test the printer.** Pair a Bluetooth printer in Android's
   Bluetooth settings first, then choose it on the tablet's Printer screen with
   its paper width, and print a test page. Check the text is clear and the code
   scans.

**When someone leaves:** Registrars → withdraw them, with a reason (district
officer for facility staff in their county; the Ministry for an officer). They
can no longer sign in to the registry. Their PIN stops unlocking each tablet at
that tablet's **next sync**, not before: a tablet out of signal cannot be told.
They stay in the directory, because the records they registered name them.
Also disable their Keycloak account. A withdrawn account cannot be added again;
someone returning is given a new account.

**When someone forgets their PIN:** Registrars → the key button beside them →
Reset PIN (the same people who may withdraw them). You never choose or learn
the new PIN. They set it themselves on a tablet while there is signal: "Set
or change my PIN" if the tablet is signed in with their account, or "Someone
else" if not. Until they do, the directory shows them as "no PIN" and they
cannot unlock a tablet. Their old PIN stops working on each tablet at that
tablet's next sync.

## Incident: a tablet is lost or stolen

A tablet holds its registrar's sign-in as a Keycloak **offline token**, valid
for up to 60 days unused. Do **both** of these, the same day:

1. **Revoke the device in NCBRS** (Devices → the device → Revoke, or
   `POST /api/devices/{id}/revoke`). Every write that names a device — sync,
   registration, corrections, BRN blocks, certificates — needs the device's
   signature and is refused from then on. Suspend instead if it may turn up
   in a drawer; suspension is reversible, revocation is not.
2. **Revoke the registrar's offline sign-in in Keycloak** (admin console →
   Users → the registrar → Sessions → sign out the offline session for
   `ncbrs-device`). Without this the token still **reads** — a name search in
   the registrar's county — for up to 60 days, because reads do not need the
   device key.

Then have the registrar sign in on the replacement tablet. A tablet that is
found again signs in afresh too: its offline token is dead once revoked.

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
- **Queue growing, link apparently up, and the node logs `District node
  configuration fault`?** It is not an outage, and it will not fix itself.
  *"The identity provider refused this node's credentials"*: correct
  `Central__ClientSecret` (or `__Username`/`__Password`) and restart the node.
  *"The centre did not accept this node's token (HTTP 401)"*: the node's
  account is not valid in the realm the centre trusts (`Keycloak__Authority`),
  or the node's clock is wrong. Either way every batch is **held, not lost**,
  and forwards once the fault is fixed; each batch's `lastError` says the same.
  Before #126 a centre 401 marked batches `Rejected` and they were never
  retried: a node upgraded from before then may hold `Rejected` batches whose
  `centralStatusCode` is 401, and those need re-queueing by hand, once the
  credentials are fixed, against the node's SQLite store (back it up first):

  ```sql
  UPDATE "ForwardedBatches"
  SET "Status" = 'Queued', "NextAttemptAtUtc" = NULL, "LastError" = 'Re-queued: rejected for the node''s own 401'
  WHERE "Status" = 'Rejected' AND "CentralStatusCode" = 401;
  ```

  The poller forwards them on its next pass. It is safe to repeat: the
  centre recognises each batch by its transaction id.
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

## Deployment: production configuration checklist

Everything a deployment must set, per service. Every value comes from the
environment or a secret store: the shipped `appsettings.json` names no
address, credential or instance (the Development file carries the local
stack's), and every service refuses to start outside Development when a link
is not encrypted and verified. **`scripts/tls-rehearsal.sh` is a working
example of all of it** — it runs each service in Production against TLS
Postgres, Kafka and Keycloak, and CI runs it on every pull request.

Never set `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` to `Development` to
get past a refusal: that also migrates and seeds on startup and allows the
throwaway signing key.

**Every service that talks to Keycloak or Postgres** needs the CA that signed
their certificates trusted by its host (on Linux, the system bundle or
`SSL_CERT_FILE`), and Postgres connections name theirs too.

### Api (`NCBRS.Api`)

| Setting | Required | Notes |
|---|---|---|
| `Database__Provider` | `Postgres` | SQLite is refused |
| `ConnectionStrings__Default` | yes | `SSL Mode=VerifyFull` (or `VerifyCA`), `Root Certificate=<path>` if the CA is not in the system store. Connect as the **application role**, never the owner (see *database privileges*) |
| `Keycloak__Authority` | yes | The realm's `https://` URL, exactly as in the tokens' `iss` |
| `CertificateSigning__PfxPath`, `__PfxPassword` | yes | The signing key (secret store or HSM): ECDSA P-256. Refused at startup without it |
| `CertificateSigning__KeyId` | yes | A production-unique id, printed in every QR. The default `ncbrs-dev` is refused at startup. On rotation, the outgoing key goes in `RetiredKeys` (public certificate only) |
| `TransferEncryption__PrivateKeyPath` | yes | The transfer key tablets seal USB transfer files to (P-256, PKCS#8 PEM, from the secret store): `openssl ecparam -name prime256v1 -genkey -noout \| openssl pkcs8 -topk8 -nocrypt`. It **decrypts personal data**, so it is a separate key from the signing key and never published. Refused at startup without it |
| `TransferEncryption__KeyId` | yes | A production-unique id, named in every sealed file. The default `ncbrs-transfer-dev` is refused at startup. On rotation, keep the outgoing key in `RetiredKeys` **with its private key**: a stick sealed before the rotation may take weeks to arrive |
| `WebClientCors__AllowedOrigins__0` | if the site is on another origin | Exact `https://` origin; wildcards refused |
| `UnauthenticatedRateLimit__TrustedProxies__0` | if behind a proxy | The proxy's IP, so `X-Forwarded-For` is believed from it and nowhere else |
| `StatutoryRegistration__WindowDays` | if the Act differs | Set in law; default 90 |
| TLS | yes | Terminate at the proxy, or set `ASPNETCORE_URLS=https://…` with `ASPNETCORE_Kestrel__Certificates__Default__Path`/`__KeyPath` |

`DeviceEnrolment__Required` and `__RequireSignature` default to on and stay on.

### Relay (`NCBRS.Relay`)

| Setting | Required | Notes |
|---|---|---|
| `Database__Provider`, `ConnectionStrings__Default` | yes | As the Api |
| `Kafka__BootstrapServers` | yes | The broker's TLS listener |
| `Kafka__SaslUsername`, `__SaslPassword` | yes | SCRAM-SHA-512 by default (`Kafka__SaslMechanism`). Or `Kafka__SecurityProtocol=Ssl` with `__SslCertificateLocation`/`__SslKeyLocation` |
| `Kafka__SslCaLocation` | if the broker's CA is not in the system store | |

### Consumer (`NCBRS.Consumer`)

| Setting | Required | Notes |
|---|---|---|
| `Keycloak__Authority` | yes | As the Api |
| `Kafka__*` | yes | As the Relay, with its own credentials |
| `ConnectionStrings__ReadModel` | yes | Its own store (the reporting replica); rebuildable from Kafka, so it needs no backup |
| `Dhis2Export__*` | before exporting | Data element UIDs and `OrgUnits__<county p-code>` (see *DHIS2 export configuration*) |
| `WebClientCors__AllowedOrigins__0` | as the Api | |

### District node (`NCBRS.District`)

| Setting | Required | Notes |
|---|---|---|
| `Central__BaseUrl` | yes | The centre's `https://` URL |
| `Central__TokenEndpoint` | yes | The realm's `https://` token endpoint |
| `Central__ClientSecret` | yes | The node's own confidential client's secret (`Central__ClientId`, default `ncbrs-district`); see *Keycloak realm*. `Central__Username`/`__Password` remain for a node signing in as an account, but a production realm should not offer that. Refused at startup without either |
| `ConnectionStrings__Default` | yes | A SQLite file on a **persistent** volume: it holds births in transit that exist nowhere else |

### Once per deployment, and after every migration

- Apply migrations as a deployment step (`dotnet ef database update`), never
  from a running service.
- Run `deploy/postgres/app-role-grants.sql` as the owner (see *database
  privileges*).
- Keycloak: the county groups and group-membership mapper (see *county
  groups*).
- WAL archiving on its own volume, and monitoring on the Api's `/health`
  `status` (see the WAL incident).

## Deployment: Keycloak realm

`keycloak/ncbrs-realm.json` is the **dev** realm and the reference for a
production one. What must differ, and what must not:

**Tablets (`ncbrs-device`) sign in once and hold an offline token.** A post
can be out of contact for weeks, far longer than an ordinary session lasts.
- Authorization code with PKCE (`pkce.code.challenge.method: S256`), and
  `offline_access` as an optional client scope. **Direct access grants off**
  (the dev realm keeps them on for the test harness, the load driver and the
  District node).
- **Redirect URIs: the app's own callback only.** That is
  `ss.gov.ncbrs.client://auth/callback`, as in the dev realm, which has since
  dropped its `*`. A wildcard lets any page receive a registrar's code.
  - Register it **exactly**, path included. Keycloak matches the whole string,
    and a bare `scheme://auth` never matches what the app sends, because .NET
    adds a trailing slash.
  - The dev realm also lists `http://127.0.0.1:53682/auth` for the Windows dev
    loop. **Leave that out of production.**
  - No web origins: a native app makes no browser requests.
- **Tablet handover needs an officer.** A district officer signs in on the tablet
  once, with an ordinary session that the app ends at Keycloak afterwards, and
  enrols it. So `district-officer` accounts need the standard flow on
  `ncbrs-device`, without `offline_access`.
- List `defaultClientScopes` explicitly (`web-origins`, `acr`, `profile`,
  `roles`, `basic`, `email`) whenever `optionalClientScopes` is listed.
  Keycloak's import gives a client that lists only its optional scopes **no**
  default scopes: its tokens then carry no roles and every tablet write is
  refused with 403. This happened while setting this up.
- Only `facility-registrar` and `community-health-worker` hold `offline_access`
  (as a composite). District officers and the Ministry sign in online and are
  refused an offline token.
- `offlineSessionIdleTimeout` 60 days, `offlineSessionMaxLifespan` 180 days:
  after either, the registrar signs in again. Shortening the first shortens
  how long a post can be out of contact without a new sign-in.

**The management site (`ncbrs-web`)** lists **no** optional scopes, so it can
never obtain an offline token.

**Each District node has its own confidential client** (the dev realm's
`ncbrs-district` is the reference): client credentials only — no browser flow,
no password grant — a secret from the secret store, and a **service account**
holding `district-officer` and the `/counties/<p-code>` group of the county it
serves. Then provision an NCBRS registrar bound to that service account's
subject (its Keycloak user id), at a facility in that county, so the centre
can attribute what the node forwards to it. A node that cannot sign in holds
every batch and logs `District node configuration fault`; nothing is lost.
With every node on its own client, direct access grants can be turned off on
`ncbrs-device`.

After changing the realm: tokens signed by a new realm key are refused by an
Api still holding the old key set until it re-fetches it (a few minutes); a
real key rotation keeps the old key published alongside the new, so this only
bites when a realm is recreated.

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
