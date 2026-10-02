# NCBRS — National Civil Birth Registration System

## What this is
An ASP.NET Core (.NET 10) Web API for a Ministry of Health birth
registration system, designed to work across hospitals, clinics, and
offline-first village health posts.

Read these first — they explain *why* the architecture looks like this, not
just what it does:
- `ARCHITECTURE.md` — the system map: the three tiers, the services and why
  they are split, how a birth flows, the event backbone, trust boundaries and
  the client-core. Start here for the whole picture; this file holds the
  decision-by-decision rationale behind it.
- `NCBRS_Draft_v1.3.docx` — the current policy/legal/technical draft.
  v1.3 reconciles the draft with the platform as built; `NCBRS_Draft.docx`
  is the superseded v1.2, kept for reference. Cite v1.3 section numbers.
- `NCBRS-Business-and-Delivery-Plan.md` — the business case plus the
  sequenced build plan (workstreams WS-A…WS-H). **Its consolidated to-do
  (§17), gap analysis (§12) and sequencing (§15) are the plan of record for
  what to build next** — prefer them over the "Not yet done" list below, which
  is only a summary.
- `NCBRS-Web-Plan.md` — the central management web front end: its plan, the
  backend gaps it forces (search, facilities, registrars, audit, CORS,
  pagination) and the phased to-do list.
- `CONTRIBUTING.md` — the delivery workflow: branch, test, review, commit,
  PR, merge, and the approval gates. CLAUDE.md carries the design rules;
  CONTRIBUTING carries the process ones.
- `docs/adr/` — decision records, for choices argued over at length: the
  options that lost and when to reopen them. 0001: sync flushes each record in
  its own savepoint inside **one transaction per batch** — not a commit per
  record, and not one flush per batch.

## Key design decisions already made (don't relitigate these without reason)

1. **Offline-first is the core constraint.** Village clinics are often
   offline for days/weeks. Every facility device must be able to register a
   birth with zero connectivity and sync later.

2. **BRN (Birth Registration Number) allocation.** The *device*, not the
   server, generates the BRN, from a block pre-allocated by the server
   whenever the device last had connectivity (`Facility.BrnBlockStart` /
   `BrnBlockEnd` / `BrnBlockNextAvailable`, and the
   `POST /api/birthrecords/{facilityId}/request-brn-block` endpoint). This
   is what prevents duplicate IDs across weeks of offline use without
   renumbering later. Do not replace this with server-generated
   auto-increment IDs.

3. **Data model follows WHO/UN vital statistics standards, not an ad hoc
   field list.** Specifically:
   - `BirthRecord.VitalEventType` distinguishes `LiveBirth` from
     `FetalDeath` (stillbirth) — a fetal death never gets a `Certificate`.
   - A live birth followed by a death is **always two records**: the
     `BirthRecord` plus a separate `NeonatalOutcome` (within 28 days,
     classified via WHO ICD-PM timing: Antepartum/Intrapartum/Neonatal) or
     `MaternalOutcome` (classified via WHO ICD-MM). Never collapse these
     into one "outcome" field on `BirthRecord`.
   - Cause of death fields are **coded** (`IcdPmCauseCode`,
     `IcdMmCauseCode`), never free text — this is what makes the stats
     usable/comparable at national scale.
   - Statistical variables (mother's education, prenatal visits, etc.) live
     in `MaternalStatistics`, kept separate from the legal `BirthRecord`
     because legally they're different functions (UN Principles and
     Recommendations for a Vital Statistics System, Rev. 3).

4. **Kafka is the event backbone**, not a plain queue. Topics:
   `ncbrs.birth-records.registered`, `.amended`, `.annulled`,
   `ncbrs.outcomes.neonatal`, `ncbrs.outcomes.maternal`, `ncbrs.sync.audit`.
   (`.annulled` is an addition to the draft's list — see Annulment below.)

   The database is always the
   system of record. A registration can no longer be delayed or affected by a
   broker outage *at all*: the request path never touches Kafka. Handlers
   stage events in the outbox (`OutboxEventPublisher`, which must be called
   **before** `SaveChanges` so the event commits with the domain write), and
   `NCBRS.Relay` drains them. That guarantee used to depend on the publisher
   being carefully fire-and-forget; it is now structural. Partition by
   district; retain 30–90 days for replay.

5. **Chain of custody / audit matters legally**, not just technically —
   every write goes through `AuditLog`, which is append-only **and enforced
   at the database** (WS-A2, see below), not by convention.

6. **A certificate is valid only if signed AND not revoked.** The signature
   proves a document was genuinely issued; it can never know the register
   was corrected afterwards, because nothing printed on the paper changes.
   So a withdrawn certificate is published to a signed revocation list
   (`CertificateRevocation`, `GET /api/certificates/revocations`) and
   verification checks both. Entries are opaque digests of the printed
   signature — never a BRN or a name — which is what lets the list be
   distributed as widely as it has to be. Do not add a serial number to the
   certificate payload to make this easier: that would change the canonical
   signed form and stop every certificate already issued from verifying.

   **A digest of the signature's *text* is only as fixed as the text, and
   anyone can change it without the key** (`CertificateSignatureForms`).
   Found on the tablet's check screen, where a code with its last character
   changed still verified: a withdrawn certificate re-printed that way passed
   the list, online and offline. Two ways, closed differently:
   - **Base64 padding bits.** A 64-byte signature's last character carries 4
     bits a lenient decoder ignores. Only the signer's own encoding now
     decodes, so a changed character is simply not genuine.
   - **ECDSA's twin.** If (r, s) verifies, so does (r, n − s). Refusing one
     would break about half the certificates already issued, since the signer
     never chose, so the list is checked under **both** serials
     (`OfflineCertificateVerifier.SerialsFor`). The text actually issued is
     always one of them.
   Nothing issued or published changes. Don't "simplify" the lookup back to
   the text as presented. The P-521 order was wrong in the first draft and
   only the test that signs under each curve and verifies the twin caught it.

## Solution layout
Three separately deployable services over one shared library. They are split
because they fail and scale differently: a broker outage stalls delivery
without touching registrations, and neither worker can take the registration
API down with it.

- `src/NCBRS.Core` — models, `NcbrsDbContext`, events, EF migrations, Kafka
  options/transport. Referenced by all three services. `Certificates/` holds
  the public-key-only verification side (payload verifier, revocation list
  canonical form, offline verifier); it is in Core rather than the API
  because the facility device app is its real consumer, and because the
  canonical signed form must not exist in two places that can drift.
- `src/NCBRS.Api` — HTTP only. Holds no Kafka producer or consumer; it stages
  events in the outbox table and nothing more.
- `src/NCBRS.Relay` — drains `OutboxMessages` to Kafka. Leases messages, so
  exactly one instance publishes a given message however many API replicas
  run.
- `src/NCBRS.Consumer` — consumes topics and updates read models.
- `src/NCBRS.District` — Tier 2 (draft 6.2, 7.2). Accepts facility sync
  batches, holds them when the national tier is unreachable, and forwards
  them **verbatim** when it returns. See the section below before changing it.
- `tests/NCBRS.Tests`

Every project keeps the `NCBRS.*` namespace via `RootNamespace`, so a file
can move between them without touching its source.

EF tooling needs both projects named, since the DbContext and the startup
project are now separate:
`dotnet ef migrations add X --project src/NCBRS.Core --startup-project src/NCBRS.Api`

That is the SQLite set. The Postgres set is generated separately, with the
provider selected so EF targets its migrations assembly:
`Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5433;..." dotnet ef migrations add X --project src/NCBRS.Migrations.Postgres --startup-project src/NCBRS.Api`
(export the variables first; the value contains semicolons). **Never pass
`--no-build` after a model change** — EF reads the stale assemblies, sees no
difference, and writes an empty migration that looks like success. A running
service also locks `bin/`, so stop it before generating. And an empty
migration is a signal worth reading: when adding an index produced one here,
the index already existed further down `OnModelCreating`.

## Deviations from the draft, already settled (don't re-open)
Draft v1.3 was revised to match these; v1.2 still contradicts them.

- **.NET 10, not .NET 8.** Also an LTS release, longer support horizon.
- **Keycloak, not ASP.NET Core Identity.** The system must manage users *and*
  client applications; realm roles and client registration come as standard,
  and the API stays a pure resource server that never stores credentials.
- **Certificate validity is signed AND not revoked.** A signature cannot
  express that the register was corrected after issuance, so revocation is a
  second, separate check — not an optimisation.

- **Corrections run on two tracks** (WS-C3, settled). The immediate track is
  exactly the clinical measurements — birth weight, gestational age, birth
  order — which describe the *event*. Everything describing *who the record
  is about* waits for a reviewer who is **not** the submitter: the child's
  name, date of birth and sex, plus both parents' names. A submission can
  split across both, and one that is entirely pending answers 202, not 200 —
  the record has not changed yet.

  Three things follow from that and must not be "simplified" away: a pending
  correction publishes **nothing** to `.amended` and withdraws **no**
  certificate (both happen at approval); approval re-checks the record and
  refuses with a conflict if it moved since submission; and rejected rows are
  kept, because a refused change is history too.

  `ApprovalRequiredFields` is a **superset** of `CertificateFields`, not the
  same list, because they answer different questions: "does this invalidate a
  printed document" vs "is this a change of legal identity". Parents' names
  sit only in the second — correcting a father's name withdraws no
  certificate but does change filiation, which is what a disputed paternity
  would be rewritten through and what an inheritance claim later turns on.
  Don't collapse the two constants back together.

## Administrative geography (South Sudan, built)
The system registers births in **South Sudan**, whose geography is
Country → State → County → Payam (rural) / Block (urban) → Boma (rural) /
Quarter (urban) → Village. The original scaffold carried a flat district
string and dev/test data from an unrelated country; both were wrong for this
deployment and have been replaced — the fixtures and dev seed use real South
Sudanese places (a fleet across Central Equatoria, Eastern Equatoria and
Jonglei) and South Sudanese names throughout.

- **`AdministrativeArea` is one self-referencing tree, not a column per
  level.** `Level` (the `AdministrativeLevel` enum) says what a node is;
  `ParentId` says where it sits. A table per level would force every location
  to the same depth, which is exactly what the draft warns against: a county
  may be split into payams *or* into blocks, and a village may be recorded
  directly under a county where the intermediate area was never captured.
  Payam and block share a tier, as do boma and quarter — the same tier carries
  a rural and an urban name, and a place is one or the other. `CanContain`
  allows any strictly-higher tier as a parent, not only the one immediately
  above, which is what keeps the tree flexible.
- **It lives in `NCBRS.Contracts`** (BCL-only), with the `AdministrativeLevels`
  rules beside the model, so the facility device app can validate a tree
  offline exactly as the centre does — the same reason the certificate
  verification side sits there.
- **`Code` is the stable identity, not the surrogate id.** Audit rows, the
  reporting projection and DHIS2 org-unit maps snapshot/reference the code, so
  a name correction never orphans a snapshot or a mapping. Codes are the
  **official p-codes** and unique across the tree: `SS` (country), `SSxx`
  (state), `SSxxyy` (county), `SSxxyyzz` (payam) — Central Equatoria `SS01`,
  Juba `SS0101`.
- **Access scope is the County** (not "country", not the old flat string).
  `CountyScopeResolver` resolves the caller's county by walking up their
  facility's area chain, and `CountyLookup` resolves the county code the same
  way (falling back to `Facility.CountyCode`, else `Unknown`) — audit, events
  and the reporting stamp all carry that resolved county. Ministry admins stay
  national; scoping still **refuses** rather than silently narrows.
- **The district → county rename is complete** (PRs #60–#64). `Facility.DistrictId`
  was **renamed to `Facility.CountyCode`, not dropped**: it is the flat, indexed
  scope key that SQL filters need, because a variable-depth tree walk cannot be
  a `WHERE` clause. It is a deliberate denormalisation of the county the tree
  resolves to, and it coexists with the nullable `AdministrativeAreaId`, which
  records the precise location. `AuditLog`, `DeviceAlert` and the read-model
  facts carry `CountyCode` too.
- **Event wire names are pinned.** `BirthRegisteredEvent` and the amendment
  events name the field `County` in C#, but `[JsonPropertyName("DistrictId")]`
  keeps the JSON name, so events already in the topic still replay. Don't
  "tidy" the attribute away — that silently breaks replay of history.
- **Deliberately not renamed:** the `district-officer` role
  (`NcbrsRoles.DistrictOfficer`, tied to the Keycloak realm) and **the "District
  tier" (`NCBRS.District`), which is a different thing entirely** — a
  store-and-forward deployment node, not an administrative field. Don't conflate
  the two.
- **The seed never invents locations.** `SouthSudanAreas.json` (an embedded
  resource) is generated from the official **COD-AB** (OCHA / NBS, 2020 vintage)
  and marked `"verified": true` with its provenance: the country, 11 admin-1
  areas (10 states plus the Abyei Region, `SS00`), 79 counties and 512 payams.
  Vintage caveats: Pibor is a county (`SS0308`) under Jonglei, and Ruweng is
  absent. **Bomas are not seeded** — they are not in the official dataset, so
  they are created as needed and never invented. `AdministrativeAreaSeeder` is
  idempotent (upsert by `Code`) and seeds Country/State/County/Payam; it runs
  after migrations on Development startup, before the dev data seeder. Note the
  dev seeder is keyed by subject and **never rebinds** an existing registrar,
  so a database seeded before a seed change keeps the old bindings — reset it
  rather than expecting a restart to correct it — `scripts/dev-reset.ps1`
  does that safely (backs up first; clears Kafka too, or the read model replays
  the old events).
- **`GET /api/administrative-areas`** feeds dependent location pickers one
  level at a time (`?parentId=` for children, `?level=` for a whole level,
  neither for the country root). Read-only and open to any signed-in caller:
  it names no person and is the same reference data a registrar and an
  oversight officer both need. Creating areas is a separate, write-gated
  concern and is not this endpoint.
- **Migrations are per provider, as everywhere** — the new table, FK and
  columns were added to both the SQLite set (Core) and the Postgres set
  (`NCBRS.Migrations.Postgres`).

## Onboarding facilities and registrars (pilot readiness §1, built)
Until this, only the Development seed could create a facility or a registrar,
so a pilot district's first registrar would have been refused everywhere
(`docs/pilot-readiness.md`). Decided 2026-10-01: the Ministry creates
facilities; **district officers onboard registrars for their own county** (the
Ministry anywhere); an account **declares itself on first sign-in** and is
bound from a pending queue, so nobody copies ids and the registry holds no
Keycloak admin credential.

- **A facility's county and its BRN range are computed, never typed**
  (`FacilityOnboardingService`, `POST /api/facilities`, `CanManageFacilities`,
  Ministry only). The county is where its area sits in the tree, and it must be
  placed at a county or below. The range is the next one aligned to
  `FacilityOnboarding:BrnRangeSize` (100,000) above every range given.
- **No two facilities can hold overlapping ranges.** Aligned allocation stops
  overlap, and a filtered unique index on `BrnBlockStart` (real ranges only;
  `BrnBlockEnd > 0`) turns two simultaneous onboardings into a retry. Tests
  that gave several facilities one shared range were corrected: that is data
  the registry must never hold.
- A name is unique within its county, case-insensitively, not nationally.
- **Registrars are onboarded from a queue the accounts fill themselves**
  (`RegistrarOnboardingService`, `CanManageRegistrars`). `GET /api/me` is
  called by the web app's `AccountGate` after every sign-in; an account with
  no registrar is recorded as a `PendingAccount` from what its own token says
  (name, username, email, realm roles, its one county group). A waiting or
  withdrawn account is told so instead of meeting a 403 on every screen. If
  `/api/me` cannot be reached, the pages are shown anyway.
- **Three limits on a binding**, each closing a different misuse:
  - the facility must be in the officer's county (`CanActForFacilityAsync`);
  - officers bind facility staff only, because an officer who could create
    officers could widen their own oversight;
  - the role must be one the account **holds in Keycloak**, and the account's
    county group, if it has one, must be the facility's. A district officer
    needs a county group, or every dashboard would refuse them.
- **The queue never publishes the Keycloak subject.** A waiting account is
  bound by its own `PendingAccountId`, as the directory has always held (the
  subject identifies an account to Keycloak, and callers start keying on it).
- **Withdrawal** keeps the row, because the trail names them.
  `CurrentRegistrarService` resolves a withdrawn registrar as nobody, and the
  tablets' PIN bundle leaves them out, so their PIN stops unlocking each tablet
  at its next sync. The directory shows *when* they were withdrawn but not
  why: anyone may resolve a colleague by id, and the reason is an HR matter.
  A withdrawn account cannot be bound again, since subjects are unique per
  registrar; someone returning gets a new account.
- The procedure is in RUNBOOK.md, "Onboarding: a facility, its staff and its
  tablet".

## The fuller registration (2026-10-02, registry built; forms next)
The registration now records the place of birth, the child's given names and
surname, both parents in detail (names, the mother's maiden surname, date and
place of birth, job, address, an identity document), the parents' marriage,
and proof of address. Decided with the user: **documents as type and number
only** (no photos: heavy to sync, and ID copies on a lost tablet are an
exposure), **jobs as free text** (the coded ISCO groups in maternal statistics
stay what national figures count), and **both forms**.

- **Required:** date of birth, place of birth, the child's given names and
  surname, and sex. Everything else is optional; a parent's details must name
  them.
- **`FullName` is composed from the parts** (`PersonNames.Compose`) and stays
  what the signed certificate, duplicate matching, search and corrections read.
  So nothing already signed or matched changes. Stored parts are whitespace-
  collapsed the same way, so a part always matches the whole.
- **The original one-piece form is still accepted** (`ChildFullName`, and the
  parents' `MotherFullName`/`FatherFullName`) until every tablet is upgraded.
  Naming the child in parts is what makes a request the fuller form, which
  also requires the place of birth. A parent named both ways is refused.
- **The tablet's `RegistrationRules` restates every new rule word for word**,
  held there by `RegistrationRulesParityTests`. That test now also checks each
  case is refused or accepted as its name says, because two sides agreeing on
  *nothing* would pass.
- **The lookup by exact BRN withholds addresses, document numbers and
  certificate references** from callers who may not act for the facility
  (`RegistrationDetails.Restricted`). That lookup is deliberately open to any
  signed-in caller, so it could not simply carry them. Names, dates and places
  stay visible, as the record header's names always were.
- **Nothing new reaches the event stream or the certificate.** The registered
  event carries no names (draft 4.2, minimisation). Adding place of birth to
  the signed certificate would change its canonical form, which is a separate
  decision.
- **A correction to a full name clears its given names and surname**
  (`AmendmentService.ForgetNameParts`) rather than leave them contradicting
  it; correcting the parts themselves is a follow-up.
- South Sudanese law has no civil partnership, so there is only the marriage
  (statutory, customary or religious).
- Enum columns are stored as text, like every other enum here.

## Signing key rotation (WS-A3/A4, built)
The signer holds one **active** key and any number of **retired** ones;
`CertificatePayloadVerifier` holds a set and selects by the key id printed in
the QR.

**Retirement is not compromise, and the difference is the whole design.** A
certificate signed under a retired key stays valid — it was genuinely issued,
and rotating a key says nothing about the birth it certifies. A *compromised*
key means everything it ever signed is suspect, which is a mass revocation
and a different act; it is **not** expressed by dropping the key from the set,
because that would make genuine and forged certificates fail identically.

- **Retired keys are public certificates only** (`CertificatePem` or
  `CertificatePath`). The private half of a retired key has no business on an
  API server — it can still mint certificates that verify, and nothing here
  needs it.
- **The key id selects the key**; keys are not tried in turn. Trying them all
  would let a signature made under one key validate a payload claiming
  another.
- **Reusing the outgoing key id is refused at startup.** A rotation that
  reuses it leaves no way to tell which key signed a given certificate.
- **A verifier with no keys is refused at construction** — it would reject
  everything, genuine and forged alike, which is indistinguishable from the
  system being broken.
- **`/signing-key` and `/offline-bundle` publish the whole set.** A device
  provisioned with only the current key rejects every certificate signed
  before the last rotation. This is why the bundle refresh in WS-B8 matters:
  a device that has not refreshed keeps working for its own era and correctly
  refuses what it cannot check.
- **A revocation list signed by a key the device does not hold is
  `Untrusted`**, not ignored — it cannot tell a genuine list signed by a new
  key from a forged one. Refreshing the bundle resolves it.

## District tier (draft 6.2/7.2, built)
`NCBRS.District` is a store-and-forward relay on a mini-PC in a district
office, so a village post's sync does not depend on the national tier being
reachable at that moment.

- **It forwards batches verbatim and never reinterprets one.** A second place
  that understands registration is a second place it can drift from the
  centre. The node parses only what it must to deduplicate and route:
  transaction id, device, facility, record count.
- **It holds no copy of the register.** `DistrictDbContext` has one table, of
  batches in transit. A district box holding birth records would multiply
  where they live, onto hardware far easier to reach than a data centre —
  draft 4.2 asks for minimisation. This is a deliberate refinement of the
  plan's "sharing the Core schema": the tiers share the sync *contract*, not
  the registry tables.
- **Queued is 202, forwarded is 200.** The centre can say what became of each
  record; a node that has not reached it can only say it is holding the
  batch. A device told "queued" must not tell a family the registration is
  confirmed, so the distinction survives into the status code.
- **It authenticates as itself, not by replaying the device's token.** Tokens
  are short-lived and a batch may sit for days; a node hoarding bearer tokens
  on district hardware is a worse exposure than the problem it solves.
  Attribution survives regardless — each record carries its author, which the
  centre validates, and the node is genuinely the uploader. **"Itself" is its
  own confidential Keycloak client** (`ncbrs-district`, #129): client
  credentials (`Central:ClientSecret`), a service account holding
  `district-officer` and its county group, and an NCBRS registrar bound to that
  service account's subject, so every batch it forwards is attributed to the
  node (the rehearsal checks it). It used to sign in through the tablets'
  client with a person's password — which a production realm cannot allow once
  the tablets' password grant is off. The dev seed and realm are held to
  exactly one registrar per realm user, the node's service account included.
- **"The centre said no" ≠ "the centre did not answer".** Only 4xx (except
  401/408/429) stops retrying; everything else stays queued. Conflating them
  would discard births whenever a link drops.
- **A 401 is about the node, not the batch** (#126). It means the centre did
  not accept the node's own token — a realm change, a clock skew, a rotated
  credential — and it used to mark every batch `Rejected`, never retried,
  silently dropping births out of the queue. It is held, the cached token is
  dropped, and the fault is named. A **403** stays a refusal: that is the
  centre declining *this batch* (an unenrolled device, a failed signature).
- **A credentials fault is not an outage.** A token request the identity
  provider refuses (400/401/403) was recorded as "centre unreachable" and
  logged nothing, so a node with a wrong password looked like one waiting out
  an outage forever. Both it and the centre's 401 are now logged as errors,
  once a minute (`ConfigurationFaultLog`, a singleton because the typed client
  is created per poll), and the batch's `LastError` says what to fix. Outside
  Development a node with no usable credentials — username and password, or a
  client secret — refuses to start. The TLS rehearsal runs a node with a wrong
  password and checks it holds the batch and says why.
- **Idempotency is end to end** via the caller's transaction id, carried
  through unchanged. Proven live: the centre received one transaction twice —
  once forwarded, once direct from the device — and produced one batch and
  two records. **The id is read from where the centre reads it** —
  `meta.transactionId`, or failing that the `X-Transaction-Id` header, body
  winning — **and forwarded as the header on every attempt.** The node used to
  read only the body and never forward the header, so a batch named in the
  header was refused here (as a 500) though the centre accepts it, and would
  have reached the centre with no id, making each retry new work there.
- **A malformed batch answers 400, never 500.** `TryRead` checks each
  property's JSON type before reading it: the `JsonElement` accessors throw on
  the wrong type rather than returning false, so `"meta": null` or a numeric
  id crashed the request. Both faults were found by the TLS rehearsal, the
  first thing to send the node a batch not shaped like its own tests.
- **No authentication of its own.** The centre authenticates every batch
  properly, and a second identity system on a district box would add a place
  credentials live without adding a check. Device enrolment (WS-B9) is what
  gates this hop: the device signs the batch and the centre verifies the
  signature the node forwards.
- **It carries the device's signature with the bytes it covers** (plan §17
  11c). The body is read as raw bytes — never bound as JSON and re-rendered —
  and `X-NCBRS-Device-Signature` is stored beside it (`ForwardedBatch.DeviceSignature`)
  and forwarded unchanged. Before this the node dropped the header, so with
  signing on (the default) the District tier delivered nothing.
- **One forward at a time per batch** (11b). Every forward goes through
  `BatchForwarder.ForwardAsync`, which **claims** the row (`NextAttemptAtUtc`
  past the attempt's timeout) and saves before the request goes out; the
  controller claims a new batch in the same write that stores it. Without the
  claim the poller found a batch the controller was forwarding "due" and sent
  it again, and the centre's `409` to the second copy marked a registered
  batch `Rejected`. The centre's in-progress `409` carries `Retry-After`, and
  a 4xx with `Retry-After` is "not yet", never a refusal.
- **A timeout is sized to the batch and grows** (11a). A flat 20 s cancelled any
  batch that could not fit, and cancelling it cancels the centre's transaction,
  which rolls back the **whole** batch — so the retry failed identically,
  forever, while reporting an outage. Now each attempt gets
  `Timeout + TimeoutPerRecord × records`, doubled per consecutive timeout
  (`ConsecutiveTimeouts`), capped at `MaxTimeout`; the HttpClient's own timeout
  is infinite. A timeout is reported as the centre being slow, counted as
  `slowToFinish`, and holds the queue in order like an outage does.
- **The inline forward is bound to the node's lifetime, not the device's
  connection.** A village link dropping mid-request must not cancel it — that
  would cancel the centre's transaction and throw away the batch's work.
- **The store is upgraded in place, never rebuilt** (`DistrictSchema`). It
  holds births in transit that exist nowhere else, so unlike the consumer's
  read model it cannot be deleted and replayed. Additive, nullable-or-defaulted
  columns only; anything else needs a real migration.

## Concurrent amendment conflicts (draft 6.3, built)
A device offline for weeks corrects a field the centre has since corrected
too. The draft's rule is **last-writer-wins with an audit trail, flagged for
registrar review rather than auto-merged silently** — and the second half is
the part that needs code, because the first is one assignment.

- **Detection needs the device to say what it saw.** `ObservedValues` on the
  amendment request carries what the device believed each changed field said.
  Where that disagrees with the register, the change still applies and an
  `AmendmentConflict` row is raised.
- **Per field, not per record.** A device correcting a birth weight while the
  centre corrected a name has clashed with nothing. Record-level detection
  would flag those, and a queue full of non-conflicts is a queue registrars
  learn to ignore.
- **Only fields actually being changed are checked.** A device echoing its
  whole view must not raise conflicts on fields it is not touching.
- **Silence is not a conflict.** An online caller sends no `ObservedValues`
  because it read the record moments ago; treating that as a mismatch would
  flag every ordinary correction.
- **The history records the real previous value**, not the one the device
  believed — an audit trail asserting a transition that never happened is
  worse than no trail.
- On the approval track the conflict is flagged at submission but
  `ResolvedValue` stays null: nothing has been applied, so claiming a winner
  would misdescribe the record. The approval-time drift check remains the
  gate.
- Reviewing **upholds** or marks **corrected**; there is no way to change the
  value from the conflict endpoint. Restoring the previous value is an
  ordinary amendment, which keeps one code path responsible for previous
  values, approval rules and certificate withdrawal. The row is kept either
  way — that two values existed is the fact worth keeping.

## BRN block exhaustion fallback (draft 6.3, built)
A device that runs out of granted numbers while offline issues
`PROV-{deviceId}-{sequence}` instead, and the centre assigns a real BRN when
the record syncs.

**This is the one knowing departure from decision #2's "no placeholder IDs,
no renumbering later", and the departure is narrower than it looks:** a
provisional identifier is deliberately *not* a BRN and never pretends to be
one, so assigning a BRN at reconciliation is numbering the record for the
first time, not renumbering it. That is why the prefix is loud. Do not make
it subtler.

The alternative is worse both ways: a device that stops registering sends
families away from the only health worker they may see for weeks, and one
that keeps counting past its block collides with a block granted elsewhere,
undiscovered for years.

- **The flag is never lost.** `ProvisionalIdentifier` is retained after a real
  BRN is assigned, and `GET /api/BirthRecords/{brn}` resolves on **either** —
  a family may still be holding the slip the device printed.
- **No certificate until reconciled.** Signing over an identifier that is
  about to be replaced would leave a signed document whose subject the
  register stops knowing by that name.
- **Numbers come from `BrnBlockNextAvailable`**, the same `[ConcurrencyCheck]`
  counter that grants device blocks, with the same retry loop — so a
  reconciled record can never be handed a number a device already holds.
- **"Reconciled, not silently merged"** means the act is audited as
  `ReconcileProvisional:{identifier}` against the new BRN, not that a human
  must press a button. Assigning the next number from a range the facility
  already holds is mechanical; a queue would leave families uncertificated
  for no gain. A malformed `PROV-` value is refused outright — it is neither
  a BRN the centre can reconcile nor a fallback it can recognise.
- If the facility's own range is exhausted centrally, the record **keeps** its
  provisional identifier and waits. Extending the range is a central act, not
  a reason to lose the birth.

## Maternal statistics (draft 6.5.1, built)
The UN P&R Rev. 3 questionnaire. `PUT /api/BirthRecords/{brn}/maternal-statistics`
upserts it, and it can also ride on the registration request — one facility
workflow, as the draft describes.

**The separation from the legal record is behavioural, not just a table
boundary.** It must keep holding:

- Nothing here gates a certificate, blocks a registration, or reaches the
  printed document. A birth whose questionnaire is blank is a fully
  registered birth. A questionnaire supplied at registration that turns out
  contradictory is dropped, never taking the birth down with it.
- Revision needs no approval workflow, unlike an amendment. This is not
  anyone's legal identity; a corrected statistic is just a better statistic.

**Every field is coded or numeric — none is free text.** The original scaffold
had `MotherEducationLevel` and occupations as `string?`, which is unusable at
national scale ("farmer", "subsistence farming" and "agriculture" are three
answers to one question). They are now `EducationLevel` (ISCED 2011 levels,
not years — years are not comparable across systems) and `OccupationGroup`
(ISCO-08 major groups, the coarsest level a registrar can assign untrained).
`NotStated` is a real answer and stays distinct from null, which means the
question was never put.

Cross-field consistency lives in the service, not the validator, because it
needs the birth record to compare against — a last live birth after this one,
antenatal care beginning outside any plausible pregnancy, or care that began
with zero contacts (the booking visit is itself a contact). A questionnaire
that contradicts itself is worse than a blank one: it aggregates silently
into a national figure nobody can tell is wrong.

`BirthIntervalMonths` and `MeetsWhoAntenatalMinimum` (8 contacts, WHO 2016)
are derived on read, not stored — both are WHO indicators in their own right.

## Duplicate matching: names are compared word by word (plan §17 11e)
`DuplicateMatcher` scores two records as possibly one child. Its name
comparison used to be whole-string edit distance, which rewards **any** shared
word. In this registry that's routine: a name is a given name plus the
father's name, so siblings, cousins and a county's worth of Dengs share one
word, and the common given names recur constantly. Every "duplicate" the demo
seed produced was two different people sharing one word, one of them scored 94.

- **A whole word that disagrees makes two different names.** The words are
  aligned one to one, in whatever order matches best. If any aligned pair of
  real words (3+ letters) is under 50% alike, the name scores nothing. A
  misspelling keeps a word recognisable (Deng/Deeng 80%); a different name
  doesn't (Achol/Aluel 40%).
- **A missing word or a swapped order is not a difference.** The similarity
  is the better of the whole string and the word-by-word alignment, so "Deng
  Ayen" matches "Ayen Deng", which the whole string alone never did.
- **The child's second name is the father's.** A father recorded differently
  still leaves the same birth flagged when the mother's name agrees, which is
  why the mother carries more weight than the child.
- **`DemoSeedTests` pins the seed to exactly one candidate**: the duplicate
  `DemoBirthSeeder` plants on purpose (a village-post birth re-registered at a
  hospital, with the second name respelt and the date one day off). If a
  matcher change makes the seed flag more, it has reintroduced false positives.
  If it flags none, it has lost the case the feature exists for.
- **Not done, and it needs data:** weighting a shared *rare* word above a
  shared common one. Don't tune that on synthetic names.

## Annulment (built)
Voiding a registration that should never have existed. Three acts must stay
distinct and must not be collapsed into each other:

| Act | Says | Survives |
|---|---|---|
| **Amendment** | the register described a real birth wrongly | the record, corrected |
| **Duplicate supersession** | two records describe one child | one of them |
| **Annulment** | there was no such birth | nothing |

Consequences, each load-bearing:

- **Ministry-level only** (`CanAnnulRegistrations`), unlike amendments and
  duplicates which are district calls. This withdraws a legal identity rather
  than deciding how it reads.
- **`ncbrs.birth-records.annulled` is its own topic** — an addition to the
  draft's 6.4.1 list, which predates this path. Do **not** fold it into
  `.amended`: a consumer handling an amendment *updates* its copy, one
  handling an annulment must *void* it. Conflating them would leave a
  National ID record standing for an identity the register has withdrawn,
  which is the single worst outcome here.
- **Every acting path refuses** — certificate issue, reprint, amendment,
  outcomes — and the record is excluded from duplicate matching. All answer
  409: nothing is wrong with the request, the record it names is void.
- **Nothing is deleted and the BRN is never reissued.** A number that has
  circulated must keep resolving to an explanation, so `GET` still returns
  the record with an `annulment` block.
- **There is no un-annul.** If an annulment was itself wrong the remedy is a
  fresh registration, which leaves both acts visible.

## BRN confirmation (draft 5.1, built)
The centre reconciles every submitted BRN against the blocks it actually
granted (`BrnReconciler`): the number must parse, fall inside the facility's
`BrnBlockStart`–`BrnBlockEnd`, and be **below** `BrnBlockNextAvailable`,
which is the first number not yet handed to any device. Only then is
`ConfirmedAtUtc` set and the record `Confirmed`.

This is what makes decision #2 a guarantee rather than an honour system.
Devices generate their own BRNs — they must, a post offline for weeks cannot
ask — but nothing about that requires the centre to accept whatever number
arrives. Unchecked, a device could invent identifiers that collide with a
block granted to another facility next month, surfacing years later as two
citizens holding one registration number.

Two rules not to "tidy away":

- **An unconfirmable BRN is never refused.** The birth happened and the
  number may already be printed on a provisional certificate in a family's
  hands; rejecting it would force exactly the renumbering the block design
  exists to avoid. The record stays `Provisional`, the reason is audited as
  `BrnUnconfirmed:<reason>` against the device that sent it, and the sync
  response tells the device `brnConfirmed: false`.
- **`ConfirmedAtUtc` is separate from `Status`.** `Status` is one enum and an
  amendment overwrites it with `Amended`, which would otherwise erase the
  fact that the record had been reconciled.
- **A grant skips numbers already on a record.** `BrnBlockNextAvailable`
  tracks what has been *granted*, not what has been *used*, and the two
  diverge whenever a record enters carrying a BRN from the range without a
  grant — a sync from a device provisioned elsewhere, a restored dump, a
  seeded environment. The counter never learns, so the next grant hands out
  numbers that are already registered. Found live: a facility sitting at
  200000 while 200000 and 200001 were both on records.
  **Checked at grant time rather than maintained on write, deliberately.** The
  alternative — advancing the counter when a record arrives with a BRN above
  it — would let a device's own number move a facility's counter, so one
  device with a bad clock could burn a whole range with a single high value.
  Decision #2 and this section both turn on the centre never trusting a
  device-supplied number, and asking the register what it holds keeps that
  intact. Skips are audited as `BrnBlockGranted:skipped=N`, because a counter
  behind the register means records reached the range outside the grant path
  and somebody should be able to see that. Provisional identifiers are never
  treated as used: a `PROV-` value was never drawn from a block and cannot
  collide with one.

## Late registration (WS-C1/C2, built)
A birth registered outside the statutory window (`StatutoryRegistration:WindowDays`,
default 90 — the number is set in law, which is why it is configuration)
requires coded evidence and a declarant, and a district registrar other than
the filer must verify it.

Two things here are easy to get wrong and must not be "simplified":

- **The window is measured to the device's capture time**
  (`RegisterBirthRequest.RegisteredAtUtc`), not to server receipt. Measuring
  to arrival would route every registration from a post that spent weeks
  offline into an evidence-verification process it should never enter —
  turning the offline tier this system exists for into its heaviest
  administrative burden. The capture time is bounded by the birth date below
  and server-time-plus-skew above; the residual (a device claiming a capture
  time somewhere in between) is why both timestamps are stored —
  `BirthRecord.RegisteredAtUtc` next to `CreatedAtUtc`. Which one a piece of
  code reaches for decides whether it describes the registry's behaviour or
  the country's mobile coverage.
- **The window applied is stored with the timestamp**
  (`BirthRecord.StatutoryWindowDays`). The timestamp alone makes lateness
  *recomputable*, and recomputing against an amended Act would silently
  restate what was on time years ago — the same failure that put the decision
  rather than the timestamps on `BirthRegisteredEvent.WithinStatutoryWindow`.
  Keeping the window rather than a bool records the decision and its reason
  together. Both columns are null on rows written before them, and a backfill
  from `CreatedAtUtc` would assert every offline record was captured on
  arrival, which is false for exactly the tier this exists to serve.
- **The residual is audited, never refused.** Where the device's clock is what
  kept a registration inside the window on a birth that arrived outside it,
  the registration succeeds and
  `StatutoryWindowMetOnDeviceTime:{byDevice}/{byArrival}` is written. Refusing
  would close the offline tier — a post out of contact for months looks
  identical, request by request, to a dishonest one. It is only
  distinguishable in aggregate, which requires the occurrences to have been
  recorded at the one moment anybody could tell. Do not make this row fire on
  ordinary registrations: a queue that is mostly noise stops being read, which
  is the same reasoning as the connectivity-profile thresholds in WS-F4.
- **Verification withholds the certificate, not the registration.** The
  record and its BRN are created regardless — a child registered late is
  still a child who exists, and refusing the record would leave them with no
  legal identity at all. `CertificateService.IssueAsync` is where the
  process actually bites; without that the verification step is advisory and
  backdating costs a forger nothing.

Backdating is the fraud being deterred: a date of birth decides school entry,
age of majority, marriage eligibility and pension timing, and by definition
nobody contemporaneous is left to contradict a claim made years later.

## Not yet done (natural next steps)
Summary only — `NCBRS-Business-and-Delivery-Plan.md` §17 is the consolidated to-do, ordered by what unblocks each item; §12 is the gap analysis behind it.
The Tier-1 facility/village client (WS-B) is the programme's critical path.
Its **offline-first core now exists and is tested** in `client/` — BRN block
allocation, the local outbox and sync-batch settlement, offline certificate
verification, PIN unlock and device-key signing, composed by `FacilityClient`
and exercised by `client/NCBRS.Client.Harness`. **Its network layer exists too**
(`NCBRS.Client.Network`, #127): `CentralClient` makes every call the tablet
needs and reads every answer as what the device must do (succeeded, held at a
District node, in progress, refused, unauthorised, unreachable);
`ConnectivityWindow` uses a connection in the order that matters — births,
then numbers, then the verification bundle — persisting the in-flight upload
**before** sending it and resending those exact bytes until settled. Building
it found that **no upload the device built could ever have been accepted**:
`FacilityClient` sent the bare batch, which the centre refuses ("data is
required") and a District node refuses for lacking a transaction id. The
harness's `online` mode runs the device path, and the TLS rehearsal runs it in
Production through a District node and direct. What remains is the **MAUI
shell** (`client/NCBRS.Client.App`), which **builds and runs**: Android (the
tablet) and Windows (a desktop dev loop) heads, iOS/Mac Catalyst dropped. It
stays out of `NCBRS.slnx`, and CI's "Build (MAUI Android)" job builds it alone.
The encrypted store (B2) is built and tested in `NCBRS.Client.Storage`: AES-GCM,
atomic saves, and a store it cannot read is an error, never an empty device.
**The device path runs end to end on the emulator:**
1. A district officer hands the tablet over.
2. A registrar signs in once; unlocking uses the PIN, with no signal needed.
3. Births are registered, synced, confirmed, and credited to the registrar
   who unlocked.

**The registration form (B4) is a first cut, over the registry's own request.**
It is to be reworked with midwives and CHWs. The layout will change; the rules
won't, because they are the core's (`RegistrationRules`):
- **`ShapeProblems`** restates the Api's `RegisterBirthRequestValidator`.
  `RegistrationRulesParityTests` runs both over the same requests and requires
  the same fields refused, in the same words.
- **`WindowProblems`** restates the registration service's decisions: a capture
  time before the birth, and the statutory window **in both directions**, since
  late needs evidence and on-time must not carry it. The parity test sits in
  `LateRegistrationTests`, beside the real service, either side of the 90-day
  edge.
- **The window is legal configuration at the centre;** the tablet holds its
  default (`DefaultStatutoryWindowDays`), pinned to it. A deployment that
  changes the window must change the tablet too, or it sends on-time births as
  late and the reverse, and the centre refuses both.
- **Sex is never defaulted.** The enum's first value is Male, so an untouched
  form would register a boy.
- **The registrar confirms the birth, read back in words,** before a number is
  used.
- **Every problem shows at once, beside the button.** A status line at the foot
  of a long form looked like a dead tap on the emulator.

Proven there: a late birth was refused without evidence, and with it was filed
`PendingApproval` with the window stored.

**Printing (B7): both a Bluetooth thermal printer and Android's print system
(decided 2026-09-30).** The core is built (`NCBRS.Client.Printing`); the two
printer paths are the shell's.
- **Two documents, and they must never be confused.**
  - *Offline*, the tablet prints a **registration slip**: the BRN, the child,
    the facility and the registrar. It cannot print a certificate, because
    only the registry holds the signing key. The slip says it is not a
    certificate, and a provisional number is printed loudly.
  - *Online*, it prints the **certificate** the registry signed.
- **The slip's QR is `NCBRS-SLIP1.<brn>`** (`SlipCode`). It is unsigned and
  says so. Scanning it gives the number to fetch the certificate by, and the
  check screen reads it as "a slip, not a certificate", never as a forgery,
  because the family is holding exactly what they were handed.
- **A certificate prints the facts its own code proves**: read from the
  signed payload and checked against the tablet's bundle, never from the
  response's loose fields. One the tablet cannot verify, one for another
  BRN, or one withdrawn is not printed at all.
- **Fetching is issue, then reprint on 409** (`CertificateForPrint`).
  Printing again *is* a reprint, and the registry counts reprints. When
  neither works, the issue's refusal is the reason given (late and
  unverified, provisional, annulled), because it is the one that explains it.
- **Thermal output is one raster image** (`EscPos`, GS v 0 in 255-row bands),
  never printer text. Cheap Bluetooth printers have Latin fonts only and
  shape no Arabic. As dots, the slip says exactly what the screen says, in
  either language.
- Both printers lay out the same `PrintedDocument`, so they cannot say
  different things. The certificate's wording and its language are a draft
  for the Ministry, like the rest of the Arabic.
- **Android's print system is built** (`AndroidPagePrinter`). The page is HTML
  from the core (`PrintedDocumentHtml`, every value HTML-encoded), with the QR
  drawn as one SVG path. It is shown in an off-screen web view with scripts off
  and handed to the print dialog, which defaults to A4. Proven on the emulator:
  a reprinted certificate (BRN 100102) and a new birth's slip (100104). The QR
  codes decoded from the print previews are exactly the certificate's code and
  `NCBRS-SLIP1.100104`.
- **The Bluetooth thermal path is built; proving it needs real hardware.**
  - The **Printer** screen offers "page printer or PDF", or any printer
    *already paired* in Android's settings, at 58 or 80 mm.
  - The app asks only for `BLUETOOTH_CONNECT`, never scan: it finds nothing
    it was not given, so it needs no location permission.
  - `ThermalRenderer` draws the document at the paper's width, so Android
    shapes the Arabic. The drawing is thresholded to dots and sent over the
    serial profile in 1 KB pieces with pauses, because cheap printers drop
    lines when their buffer overflows.
  - **Proven on the emulator:** the exact dots a printer would get, saved
    through a Debug-only preview, are legible in both languages, and their QR
    code decodes. Refusing the Bluetooth permission leaves page printing
    working and says how to allow it.
  - **Not yet proven: the Bluetooth send itself.** The emulator has no
    printer. Run "Print a test page" on a real one before B7 is called done.
  - **Debug builds only:** a "Printer (debug)" button on the unlock screen and
    the preview, so printing can be tested after a reinstall without a PIN.
    Release builds compile both out (checked).

**The tablet speaks English and Arabic (Modern Standard, a draft for Ministry
review).**
- **Where the strings live:** all 217 are in `NCBRS.Client.Core/Localization`
  (resx, strongly typed), not the app, so CI holds the two languages to the
  same keys and placeholders (`LanguageTests`). The core's own messages to
  registrars are translated too.
- **The switch** is English / العربية on the unlock and main screens. The first
  run follows the tablet's language, and the choice is remembered in
  Preferences. In Arabic the window mirrors, the Android title bar included.
- **Deliberately still English:** the registry's own reasons, the tablet's
  copies of its rules (held to the registry's words by parity tests), and audit
  reasons sent to the registry. The form shows the field in the registrar's
  language beside the rule's words.
- **Arabic-Indic digits** (٣١٠٠, ٫) are read as digits (`Language.WesternDigits`),
  so a weight typed on an Arabic keypad is not "not a number".
- **Two traps found on the emulator:**
  - **Never set `CultureInfo.CurrentCulture` in the app.** It is an
    async-local: set once at startup, it pinned the root context, and every
    later tap resolved the culture from wherever it began. The date picker
    showed an English date under Arabic labels, and the reverse.
    `Language.Use` sets only `Strings.Culture` and the process-wide
    `DefaultThreadCurrent*Culture`, which every context falls back to. A first
    fix, posting the switch to the main thread, only moved the fault.
  - **Perl edits that write any non-ASCII character re-encode the whole file**,
    corrupting every other non-ASCII character in it. Edit such files with the
    Edit tool.

**A birth the centre refuses is held and corrected, never resent unchanged.**
Before this, a refused record stayed in the outbox as it was: it was sent every
window and refused every window, forever, and the registrar was never told why.
- `SyncOutbox` now holds a refused record with the centre's reasons
  (`Refused`, persisted in `DeviceState`) and leaves it out of uploads until a
  registrar corrects it.
- `Correct` keeps the **BRN and the capture time** whatever the correction
  says. The number is on the family's slip, and the window is measured to
  capture, so fixing a typo must not make a birth late.
- **Nothing on the tablet deletes a refused birth.** It may be the only copy of
  a birth the registry never received.
- **The centre's sync refusals now name their field** (`SyncController.FieldOf`),
  as the online path always did: `lateRegistration` and `registeredAtUtc`
  instead of a flat `record`. Without it a tablet could not tell "needs
  late-registration evidence" from any other reason.
- **Correcting a birth refused on the window uses the centre's window**, read
  from its refusal (`RegistrationRules.WindowStatedIn`). The centre's ruling on
  a birth outranks the tablet's default. This is what makes a deployment whose
  window differs from the tablet's recoverable rather than a dead end.
- Proven on the emulator with the dev API at 30 days: a 45-day birth that the
  tablet thought was on time was refused, held, corrected with evidence under
  the centre's window, and filed `PendingApproval` with window 30 and its
  original capture time.
- **An offline bundle answered without keys crashed the whole window**, births
  uploaded in it included. It is now reported and the held bundle kept.

**Handover is checked against the centre, never inferred** (`TabletHandover`).
- The rule came from a live failure. A tablet revoked from the web app
  "re-enrolled" into its dead identity, because the app read a 409 as success,
  when the centre answers 409 for *any* id it holds, revoked included.
- After enrolling, the device is read back with the officer's token. Only an
  active enrolment at the chosen facility counts.
- An id the centre won't take back gets a new key, so a new id. If the old id is
  still active elsewhere, it is revoked first.
- The device id is derived from the key (`DeviceIdentity.IdFor`), so an
  interrupted handover retries as itself.
- An officer's session is one-off (`InteractiveSignIn`) and ended at Keycloak
  after the handover.
- Every sign-in URL carries `prompt=login`. The tablet's browser is shared, and
  without it the registrar is signed straight in as the officer.
- A registrar's account is checked against the tablet's facility immediately
  after sign-in. If it isn't permitted there, the sign-in is revoked on the
  spot.
- "Wrong facility: hand over again" revokes the tablet at the centre and
  confirms it before discarding the key. It is refused while any births are
  waiting to sync.

**Staff PINs come from the centre** (draft 6.7).
- A registrar sets their PIN at the centre, and the tablet provisions every
  registrar's hash for its facility. Any of them can then unlock, and each
  birth is credited to whoever unlocked.
- Wrong guesses are counted for the *device*, not per name.
- `PinPolicyParityTests` and `DevicePinCompatibilityTests` hold the tablet's
  rules and lock to the centre's real code.
- Digits are normalised to ASCII, so a PIN typed on an Arabic keypad is the
  same PIN.

**Keycloak redirect trap.** `new Uri("scheme://auth").AbsoluteUri` is
`scheme://auth/`: .NET adds the slash and Keycloak matches exactly, so the
Android redirect has a path (`ss.gov.ncbrs.client://auth/callback`).
`ncbrs-device` now allows only that redirect and the Windows loopback
`http://127.0.0.1:53682/auth`; before, it allowed `*`. **The app's data never leaves the tablet by backup or
device transfer** (`allowBackup="false"` plus data-extraction rules): restored
onto a second tablet, it would be two devices on one identity and one BRN
cursor. Cleartext HTTP is allowed to `localhost` in Debug builds only.

**Tablets sign in once and hold a Keycloak offline token (decided 2026-09-28,
#128).** A post is out of contact for weeks, far past any ordinary session.
`OfflineTokenSession` (`NCBRS.Client.Auth`) redeems the PKCE code for the
offline token, trades it for access tokens each window, **saves the rotated
token on every renewal** (Keycloak issues a new one each time), reports an
ended sign-in (`invalid_grant`) as `Unauthorized` but Keycloak being
unreachable as `Unreachable`, and signs out by revoking it at Keycloak. The
realm lets only `facility-registrar` and `community-health-worker` hold one
(composite `offline_access`), the web client none, 60 days idle and 180 days
in all. **Realm-import trap:** a client that lists `optionalClientScopes` but
not `defaultClientScopes` gets *no* default scopes — its tokens carry no roles
and every tablet write is 403. A lost tablet needs the device revoked in NCBRS
**and** the offline session revoked in Keycloak: the device key stops writes,
not reads.
- **(WS-B8)** Offline verification lives in `NCBRS.Contracts` and the client
  wraps it (`CachedVerificationBundle`, which signals when a refresh is due;
  each connectivity window refetches it when due). **"Check a certificate"**
  is on the tablet: camera QR scan (ZXing.Net.Maui, Android only) or type and
  paste, from the main screen and from the unlock screen. No unlock is needed,
  because it shows only what the paper says, checked against public keys, and
  nothing the tablet holds.
  - `CertificateCheck` (core, tested) turns the verifier's verdict into what
    the checker reads. **Four answers, and "cannot be checked here" is never
    green:** a tablet whose list of withdrawn certificates is stale has found
    nothing wrong, but cannot approve either. It also says *why* it cannot
    (`CachedVerificationBundle.CoverageAt`), because the remedy differs.
  - **Nothing a code that fails verification says is shown.** It is whatever
    someone chose to print.
  - The verifier's `Detail` is English written for the registry and is never
    shown; the reading is translated.
  - The camera is asked for when the screen opens, not at install, and typing
    works without it.

## Reporting projection (WS-A5, draft 6.4.1, built)
`NCBRS.Consumer` builds a read model from the event stream. Delivery is
at-least-once from two directions — a relay that published but crashed before
marking the row dispatched, and Kafka redelivering on rebalance or deliberate
replay — so the same event *will* arrive twice. Replay is not a fault to
engineer away: it is what 30–90 days of retention exists for.

- **Facts keyed by BRN, never counters.** "Add one" done twice is wrong and no
  care around it makes replay harmless; a row keyed by the thing it describes
  can be written any number of times and still says what it said. Totals are
  derived by querying `RegistrationFacts`, never by incrementing. This is what
  makes the projection idempotent *by construction* — it would stay correct
  with no ledger at all.
- **`ProcessedEvent` answers a different question** from correctness of
  totals: has this specific event already been acted on? Nothing here needs
  that yet. WS-E's push to the National ID Authority will, because telling an
  external system about a birth twice cannot be undone by rewriting a row.
  `Deliveries` is counted because a replay *rate* that changes is the first
  sign something upstream is wrong, and otherwise invisible.
- **Out-of-order arrival is normal, not an anomaly.** Kafka orders within a
  partition, not across topics, and registrations, amendments and annulments
  are three topics — so a replay from the earliest offset can deliver the
  whole of `.amended` before the whole of `.registered`. A correction for a
  BRN not yet seen is **held** in `PendingEvents` and applied when the
  registration arrives, ordered by when it happened rather than when it turned
  up. Dropping those would make the projection depend on arrival order: the
  same events replayed would leave an annulled birth counted as a live one.
  Proven live — a rebuild from offset 0 held 7 events and drained 5 of them.
- **A row that is never claimed stays visible.** It means a registration this
  consumer genuinely never received, which is a gap someone needs to see, not
  something to sweep up on a timer.
- **`EnableAutoCommit = false`, commit after the projection is written.**
  Auto-commit would acknowledge messages not yet applied, so a crash in that
  window would *lose* them — and idempotency defends against duplication, never
  loss. Note `StoreOffset` throws here: disabling auto-commit disables the
  offset store with it, so `Commit(result)` is called directly.
- **Its own store, not the registry's.** Draft 6.4.1 keeps the API the single
  writer to the registry, with events an outbound notification of what already
  happened; a consumer writing back would invert that. In production this
  belongs on the reporting replica.
- **`ncbrs-event-id`** carries the outbox row id as a Kafka header
  (`KafkaHeaders.EventId`). An event without it is still projected — the facts
  are idempotent regardless — but logs a warning, because it cannot be
  deduplicated.

All six topics are consumed. Outcomes and sync batches are keyed by BRN and
batch id respectively, for the same reason registrations are keyed by BRN.
Unlike a correction, an outcome is **not** held when its registration is
missing: it describes a second vital event that stands on its own, and a
perinatal death should not become invisible because the birth event has not
caught up.

## Dashboard read models (WS-F2/F3, draft §10, built)
`NCBRS.Consumer` also serves the indicator queries over the projection it
owns: `GET /api/dashboard/summary`, `/districts`, `/devices/silent`, and
`/health`. It is the same process because it is the single writer to that
store, and because putting reporting load on the registration API is exactly
what plan F1 exists to prevent.

**Who reads what (settled 2026-09-27).** A district officer reads **their own
county** only. The Ministry reads the whole country, or any one county by
naming it. The DHIS2 export is **Ministry-only** (`ncbrs-export`). This matches
the web plan's role table. Before it, any district officer could read the
national dashboard, every county's unsuppressed counts, other counties' silent
devices and the national export.
- **The county comes from the token**, as a Keycloak group
  `/counties/<p-code>` mapped into the `groups` claim (`KeycloakCounties`). It
  can't come from the registry, which this service deliberately cannot reach.
  `ReportingScope` applies the API's rule: no county group, or several, is
  refused, and naming another county is refused, not narrowed.
- **Two places hold an officer's county, so the API checks they agree.**
  `CountyClaimConsistencyFilter` refuses a district officer whose token names
  a county different from their facility's county in the registry. So a moved
  officer with a stale group finds out the first morning, instead of acting on
  one county while reading another's figures. A token with no county group
  passes the API, which scopes from the registry, and is refused here with a
  message that says why.
- **The scoped endpoints live in `ReportingEndpoints`**, not as lambdas in
  `Program.cs`, so tests call them and an endpoint that stops applying the
  scope fails a test.
- **Every Keycloak realm needs the groups and the group-membership mapper**
  (RUNBOOK, "Deployment: county groups"). A realm without them locks every
  district officer out of reporting, loudly, which is the intended failure.

**The design rule that outranks the arithmetic: an indicator whose inputs are
unknown reports null and says why. It never reports zero.** A Ministry
reading 0 neonatal deaths concludes the month went well; a Ministry reading
"not available" sends someone to find out. Every rate and share is nullable
for that reason, `DashboardSummary.NotAvailable` names the §10 indicators
this data cannot produce, and a `?? 0` added anywhere here silently converts
ignorance into a reassuring fact.

- **Timeliness is not completeness, and is never labelled as it.** The share
  reported is of *registrations* made inside the statutory window.
  Completeness asks what share of births that happened were registered at
  all — its denominator is projected births from a census, which no registry
  holds. Reporting the first under the name of the second would tell a
  Ministry its coverage is 96% when the truth might be half that, and the
  births it would be wrong about are the ones in the places the offline tier
  exists to reach.
- **Births are counted by date of occurrence** (UN P&R Rev. 3), not by when
  the registration reached the centre — which measures connectivity. Sync
  figures are counted by sync date, because those *do* measure the link.
- **A recent period is flagged `stillFilling`.** Registrations for births
  inside it are still arriving, so the figure only ever rises. Presenting one
  as final is how a dashboard shows a collapse in births every month and a
  recovery every quarter.
- **Annulled registrations are excluded from every indicator and counted on
  their own.** Excluded because an annulment says there was no such birth;
  counted because a zero nobody computed is the same failure as any other.
- **Medians, not means**, as the draft asks: one birth registered eleven years
  late would drag an average to a number describing nobody.
- **Unconfirmed records are excluded from time-to-registration and reported
  separately.** Treating a provisional record as zero days would make the
  offline tier look faster the more of its records were stuck.
- The **duplicate rate is a floor and says so in the payload**: only
  duplicates caught on the sync path reach the stream, and one refused on a
  direct online registration is announced to nobody.
- `/devices/silent` can only see devices that have synced at least once. A
  post deployed and never heard from is invisible — the worst silent failure
  here — and closing that needs device enrolment (WS-B9).

- **The delay before registration and the delay reaching the centre are
  reported apart** (`RegistrationDelay`). They are different problems with
  different remedies: how long a family took to reach a registrar is answered
  by an outreach campaign, how long the record then waited is answered by a
  mast. `TimeToConfirmation` spans both and can separate neither, and for the
  offline tier the second term can be most of the total — so reading the
  combined figure as the first says families near a village post are slow to
  register when they are not. Broken out by tier, where the two diverge most:
  a hospital terminal's sync lag is zero by construction.

`BirthRegisteredEvent` carries `FacilityTier`, `VitalEventType`,
`WithinStatutoryWindow`, `ConfirmedAtUtc` and `RegisteredAtUtc` for these
indicators. **All five are nullable on purpose**: events already in the topic
predate them, and a consumer must be able to tell "this birth was on time"
from "this event cannot say". `WithinStatutoryWindow` carries the *decision*
rather than the timestamps because the window is set in law — recomputing it
downstream next year would silently restate what was on time last year.
`RegisteredAtUtc` travels as the timestamp rather than a derived lag for the
opposite reason: nothing can change what a subtraction of two instants means.

**`RegistrationFact.PublishedAtUtc` is the publish time; `RegisteredAtUtc` is
the device's.** The former was called `RegisteredAtUtc` while holding the
publish time, next to a `BirthRecord.RegisteredAtUtc` that is the device's —
one name for two instants three weeks apart. Do not merge them back.

**The read model refuses to start when its shape no longer matches the code**
(`ReadModelSchema.EnsureUsable`). It is created with `EnsureCreated`, which is
a no-op against an existing database, so a column added or renamed simply
never appears in a store built before the change; without the check the
service starts cleanly and fails later on a dashboard query, looking like a
broken deployment rather than a projection needing a rebuild. The remedy is
always the same and always safe: delete the store and replay from the
earliest offset. Nothing is lost — this projection is derived from Kafka by
design and is the system of record for nothing.

## DHIS2 aggregate export (WS-E4, draft 6.8, built)
`GET /api/exports/dhis2?period=YYYYMM` on the consumer, in DHIS2's
dataValueSet shape. The one part of WS-E that needs no data-sharing
agreement, because it shares no personal data: the unit of the payload is a
**district-month**, never a person.

**Aggregation is not anonymity, and that is the whole design problem.** A
count of one, in a small area, for a rare event, identifies a family — and
the rarest events in a birth registry are a stillbirth and a mother who died.
Someone who already knows of one such birth in their district that month
learns the rest of the record from a published "1". Three rules follow, each
closing a different way of reading a person out of a table:

1. **A district below the threshold is withheld entirely**, total included.
   Suppressing individual cells while naming the district would still narrow
   every figure to a handful of families.
2. **A breakdown is published whole or not at all.** Suppressing one cell of
   a decomposition whose total is published is not suppression, it is
   arithmetic: 20 live births and 18 male states that 2 were female.
   Timeliness is a decomposition too, for the same reason. **What the
   published cells leave over is a cell too**, computed from the total rather
   than listed by the caller, because the caller is who forgets it: the sex
   breakdown published male and female beside a total that also held
   Undetermined, so total − male − female was the count of undetermined-sex
   newborns in a county-month, and nothing checked it. Timeliness had the same
   gap for births with no recorded window decision. Found by the item-22
   review; both pinned by tests that failed on the old code.
3. **Rare-event counts below the threshold are absent, and so are the true
   zeros.** If zeros were published and only small counts suppressed, every
   gap would mean "at least one" — the disclosure the suppression was for.
   Absence has to mean a range, not a value.

- **`MinimumCellSize` defaults to 5**, the usual threshold for health data.
  Configurable upward; lowering it is a disclosure decision, not a tuning one.
- **Data element UIDs and the org-unit map are configuration.** They are
  instance-specific, and an export hardcoded to one DHIS2's UIDs silently
  reports nothing to any other — which in DHIS2 looks exactly like a period
  with no births. An unconfigured element is skipped, never invented.
  **So the base `appsettings.json` names no DHIS2 instance at all**; the
  `DEV_` placeholders and the org-unit map live in the Development file.
  The map is keyed by **county p-code** (`SS0101`), which is what the export
  groups by. After the county rename it was still keyed by the old district
  codes, and the dev export published nothing for any period, unnoticed —
  `Dhis2ExportSettingsTests` now holds the dev map to every county
  `DevelopmentDataSeeder.SeededCountyCodes` places a facility in.
- **An unmapped district is reported, not dropped.** Its births would
  otherwise never reach the national figures and nothing would say so.
- **Suppressions are returned with the export**, not logged quietly. A
  recipient who cannot tell a withheld figure from an absent one reads the
  gap as zero, and "zero stillbirths" is a materially different claim from
  "too few to publish safely".
- Annulled registrations are excluded — sending one would report a birth the
  register has withdrawn.

**Reviewed (plan §17 item 22, 2026-10-01):**
- **Months are not cumulative, and must stay monthly.** DHIS2 rolls months up
  into quarters and years from what was published, so a withheld month never
  enters a total and cannot be recovered from one. A quarterly or yearly
  export *from this registry* would break that: year − Σ published months =
  the withheld months.
- **Revisions: settled months only (decided 2026-10-01).** A month exported
  while still filling and again later is two safe tables whose *difference*
  is not: it describes the few registrations, corrections and annulments made
  in between, and DHIS2 keeps the earlier values in its audit history. A sex
  split going from 12/12 to 13/11 reveals a sex correction in that
  county-month. So a month is exported once, after it settles: 120 days after
  it ends (`ReportingPeriod.SettlingPeriod`, the same rule as the dashboard's
  `stillFilling`, now shared). Earlier is a **409** naming the date it can go
  (`PeriodNotSettledException`), and the web page defaults to the latest
  settled month. The cost is that DHIS2 gets each month about four months
  late. Holding back small changes instead would need a durable ledger of
  what was sent, which the rebuildable read model cannot be.
- **The web page showed none of the reporting service's messages.** It answers
  `{ error }`, and the shared `toNcbrsError` read only the registry API's
  `errors` list, so every refusal on a reporting page (another county named,
  a malformed month) became a generic sentence. It now reads both shapes.
- **Cross-tabulation:** each breakdown is checked against its own total. Two
  breakdowns crossed (sex × timeliness) would need their joint cells checked,
  so a new breakdown must be checked against every existing one, not just the
  total.

## Device enrolment (WS-B9, draft 6.7, built)
Before this, `deviceId` was a string the caller asserted. Any account with
registration rights could upload an outbox under any device name, and the
audit trail recorded whatever was typed — against legal records.

`Device` is the registry of devices permitted to sync, and enforcement lives
in `DeviceEnrolmentService`, checked in `SyncController` before a single
record is touched.

**Two properties, deliberately kept apart.** *Enrolment* says this device id
is one the Ministry issued, to this facility, and has not been withdrawn.
*Possession* says the request actually came from that device, proved by a
signature over the body. Enrolment alone is an allowlist and is honest about
being one; possession is what makes a stolen token insufficient. They have
separate switches (`DeviceEnrolment:Required`, `:RequireSignature`) because a
fleet adopts them at different times — a device registry can be populated
from existing records, while signing needs every tablet to hold a key.

- **Both default to on.** A security control that ships disabled stays
  disabled; the deployment that most needs it is the one that never gets
  round to the config change. Dev sets `RequireSignature: false` because no
  device app exists yet to hold a key — **in `appsettings.Development.json`
  only.** It used to sit in the base `appsettings.json`, which every
  environment loads, so the control this bullet calls "on by default" shipped
  switched off. `TransportSecurityTests` pins the base file clean.
- **The signature covers the raw request body, byte for byte** — not a
  canonical projection of its fields. A canonical form is a second
  description of the payload, and the day it disagrees with the parser a
  genuine batch fails or a tampered one passes. This also survives the
  district tier, which stores and forwards a batch's raw bytes and its
  signature header unchanged; both designs come from the same rule, that an
  intermediary must not need to understand a batch to carry it. **This is the
  gate the District section says was missing.** (It was open until §17 11c: the
  node carried the bytes but dropped the header.)
- **Buffering is enabled only for requests carrying the signature header**, so
  the largest payload the system takes — a post offline three weeks — is not
  buffered for every other call.
- **Re-enrolling an existing device id is refused (409), not treated as a key
  update.** Silently replacing the key takes over an identity every record in
  the register is already attributed to. Replacing a device means revoking
  first, which leaves both acts in the trail.
- **A private key offered at enrolment is refused outright**, not trimmed to
  its public half: a device whose private key reached a server has a
  signature that proves nothing, and accepting it quietly would leave
  everyone believing otherwise.
- **Suspension is reversible, revocation is not.** Most "lost" tablets turn up
  in a drawer, and forcing re-enrolment for that teaches districts not to
  report them missing. There is no un-revoke — the remedy is a fresh
  enrolment with a fresh key, leaving both acts visible.
- **A revoked device is refused even when enforcement is off.** The flag is a
  migration aid, not an amnesty.
- **Refused batches are audited** (`DeviceRefused:{outcome}`) even though
  nothing is registered. An attempted upload from a stolen token is the most
  interesting thing the endpoint sees. **Record refusals through
  `RefusalAudit`, never `db.AuditLogs.Add`.** Devices send a transaction id,
  so their requests run inside the idempotency filter's transaction, which
  rolls back on any non-2xx — and a refusal written with the work was rolled
  back with it, so the promised audit silently never happened for real
  devices (found and fixed with the register work below; the tests had called
  the controller directly and never met the filter). `RefusalAudit` re-writes
  only its own rows after the rollback, with the change tracker cleared first:
  keeping *every* audit row from a failed request would be worse, since an
  ordinary action's row would then assert work that never happened.
- **`LastSeenAtUtc` is null until a device first syncs**, which is what makes
  a deployment that failed silently visible — previously a post that never
  reported looked like a quiet area. This is what device silence alerts
  (below) are built on.

**Online registration is held to the same proof** (per-request signing, chosen
over mTLS). `POST /api/birthrecords/register` used to take its `deviceId` on
trust, so a stolen token could register as any device — even a revoked one.
The **channel comes from the token, not the body**: its `azp` names the OIDC
client it was issued to (`DeviceEnrolment:WebClientId`, `ncbrs-web`).

- A **web-client** token is the management site: it registers as `ncbrs-web`,
  holds no device key, and the user's interactive session is the authority
  (the realm gives that client no password grant). It may **not** claim a
  device id.
- **Any other** token is a device and goes through `DeviceEnrolmentService`
  exactly like a sync batch — enrolled, active, at this facility, and signed
  over the exact bytes when `RequireSignature` is on. It may **not** claim
  `ncbrs-web`: that is the obvious bypass (skip the signature by claiming to
  be a browser), and it is refused rather than left to enrolment settings.
- A proved device is marked seen, as on sync. Refusals are audited through
  `RefusalAudit` (above).

**Every other write that names a device is held to the same rule** (plan §17,
9a): correction, BRN block request, certificate issue and reprint, the
maternal questionnaire, and both outcomes. They were "attribution labels",
but the label *is* the audit trail on a legal record, and a stolen token could
correct a child's name or draw a facility's BRN range as any device it named.
One `DeviceChannelGate` applies the rule everywhere, so the refusal — its audit
row, its survival past the rollback, its 403 — cannot drift between endpoints.

- **On an existing record, the record's facility decides**, not anything in the
  body: a device enrolled elsewhere is `WrongFacility`. A number that resolves
  to no record passes the gate so the endpoint answers its own 404 — a refusal
  would assert a record the register does not hold.
- **A BRN block request's `deviceId` is optional only for the management
  site.** A device must name itself: a block nobody can attribute is a
  fortnight of registrations nobody can attribute either.
- **"Seen" is saved by the gate**, not left for the act to commit — a device
  that proved itself reached the centre whether or not what it asked for
  succeeded.
- `ncbrs-web` lives once in the web client (`web/src/auth/channel.ts`); five
  per-screen copies were five places to break the rule with one edit.

**District officers act within their own county, and no further.** Oversight
roles used to be national for *writes* while county-scoped for *reads*, so a
district officer could approve an amendment, verify a late registration,
issue a certificate or enrol a device for a record in a county they were not
permitted even to search. `CurrentRegistrarService.CanActForFacilityAsync` now
applies the read rule to every write: own facility always; ministry admin
nationally; district officer only where both counties resolve and match.
**It fails closed** — an unresolvable county on either side refuses, because
"unknown" matching "unknown" would reach every facility the hierarchy cannot
place. Acknowledging a device alert is held to the same rule: acknowledging
from another county would tell the alert's own district it is being handled.
The old flat `NcbrsRoles.CrossFacility` list is gone on purpose; don't
reintroduce a role list as a shortcut past the county check.
The device *reads* follow `CountyScopeResolver` too: the alert queue and
the device list (with no facility named) narrow to the officer's own county,
and fetching one device by id refuses another county's (it used to bypass
the rule),
and naming another county is refused rather than silently narrowed — a quiet
narrowing would read as "nothing is wrong over there".

**The review queues and duplicate review follow it too** (found by the
pre-audit authorization sweep, plan §17 24).
- **Duplicate review had no county check at all.** That is the review that
  supersedes a whole registration and withdraws its certificate, so any
  district officer could do that anywhere. `ReviewAsync` now takes the
  permission check as a **required** delegate, with no default, since a
  default of "allow" is the bug. It checks **both** records, so a pair
  spanning two counties is the Ministry's call.
- **All four queues were national for reading:** pending corrections,
  amendment conflicts, late registrations and duplicates. An officer could
  not act on another county's items, but could read the proposed name
  changes, evidence and children's names. `ReviewQueueScope` applies the
  resolver to all four, and each query takes the county as a **required**
  parameter (null only for the Ministry), so no future caller gets the whole
  country by forgetting it. The duplicate queue shows only pairs wholly
  inside the county, matching who may decide them.
- **Deliberately not scoped:** `GET /api/BirthRecords/{brn}`, the lookup by
  exact number. A BRN is carried by the family between facilities, and the
  surveillance risk is name search, which *is* scoped. Recorded for the
  auditor as a choice to confirm, not an oversight.

## Sealed USB transfer files (WS-H2, built)
A post with no network at all carries its births on a USB stick to a
connected sync point. The signed transfer envelope gave integrity (a changed
file is refused) but **no confidentiality**: names and dates of birth sat in
it as base64, on media that passes through several hands. Now a file is
**signed, then sealed** to the registry.

- **`SealedTransfer` (Contracts)** is ECIES over P-256: a fresh ephemeral key
  per file, ECDH with the registry's key, HKDF-SHA256, and AES-256-GCM. The
  version, key id and ephemeral key are bound in as associated data. That
  binding is mutation-checked: without it, a file relabelled with another key
  id opened.
- **A fresh key per file** means no two files share a key, and a tablet holds
  nothing that opens a stick, not even one it sealed.
- It lives in Contracts, like `DeviceSignature`, because the tablet seals and
  the registry opens. `TransferEnvelopes` (the signed envelope) moved there
  for the same reason.
- **The transfer key is not the signing key.** One proves documents genuine
  and is published; the other decrypts personal data and never leaves the
  registry.
  - `TransferKeyring` is loaded and resolved at startup (the #125 lesson). It
    is refused outside Development without `TransferEncryption:PrivateKeyPath`,
    or with the development key id.
  - Development may use a throwaway key.
  - Unlike retired *signing* keys, retired *transfer* keys keep their private
    halves: a stick sealed before a rotation may take weeks to arrive.
- **The public half rides in the offline bundle** (`TransferKey`), which a
  tablet already refreshes. It exports exactly when it cannot reach the
  registry, so it must already hold the key.
- **`POST /api/Sync/transfers`** takes the sealed file as the usual
  `ApiRequest` data (it is JSON). It opens the file, checks the envelope's
  device matches its batch, then runs **the same pipeline as a sync**
  (`ProcessBatchAsync`): the signing device's enrolment and signature, the
  uploader's permission for the facility, per-record processing. The audit
  says how the batch arrived (`SyncBatchProcessed:SealedTransfer:{keyId}`).
- Uploaded twice (two sticks, or a stick and a later sync), the births are held
  once, since records deduplicate by BRN.
- **The tablet side is built too.** "No signal? Save births for a USB stick or
  card" (`FacilityClient.BuildSealedTransferFile`) seals what is waiting to
  sync and hands it to the share sheet.
  - The births **stay queued**, because a stick can be lost. The tablet shows
    how many from the last export the registry has not yet confirmed.
  - A window fetches the bundle whenever no transfer key is held, not just
    when it is due. Otherwise an upgraded tablet with a fresh bundle would
    wait days, unable to export.
- **The web's "Upload a transfer file" page** (`/records/transfer`,
  `CanRegisterBirths`) passes the file on unread and shows each birth's
  outcome. It tells the officer that refused births are the tablet's to
  correct.
- Proven on the emulator: two births exported unsynced. The sealed file names
  nobody and no BRN. Uploaded by the district officer, the births were
  Confirmed, credited to the registrar who registered them, and audited as
  arriving by sealed transfer. The tablet's next sync settled them.

## Device silence alerts (WS-F4, built)
"A silent device is indistinguishable from a district with no births, and only
one of those needs intervention" — the plan's own note, and the whole
justification. A post whose tablet died simply stops producing registrations,
and every dashboard reports that as a quiet month. The births still happened;
nobody has them.

`DeviceSilenceMonitor` sweeps on a timer and raises `DeviceAlert` rows;
`GET /api/devices/alerts` is the district's queue and
`POST /api/devices/alerts/{id}/acknowledge` records that someone is acting.

- **The threshold follows the facility's `ConnectivityProfile`, never one
  number for the fleet.** A village post on `OfflineFirst` routinely goes a
  fortnight without connectivity — alerting at three days buries its district
  in notifications about posts working exactly as designed, and a queue that
  is mostly noise stops being opened. A hospital on `AlwaysOn` silent for
  three days is already a fault. Defaults: 2 / 7 / 21 days. Proven live — a
  post and a hospital terminal both 10 days silent, only the hospital alerted.
- **`NeverReported` is a separate kind from `Silent`** because the remedy
  differs: a device that used to sync and stopped is a link or a battery; one
  that has never synced is a deployment that failed at handover. Folding them
  together sends an officer to diagnose a network problem that was never the
  problem. It is measured from enrolment with a grace period, so a tablet
  enrolled this morning is not a failure.
- **It runs against the registry, not the reporting projection.** The
  projection can only see devices it has heard from, so it structurally cannot
  report one that has never sent anything — the worst case, not an edge case.
  `/api/dashboard/devices/silent` on the consumer remains useful for sync
  volume, but this is the authoritative view.
- **Acknowledging is not resolving.** Acknowledging says "I know, I am driving
  out there Thursday"; only the device reporting again resolves it. If an
  acknowledgement closed the alert, a district could empty its queue without a
  single device coming back — precisely the gap this surfaces. Resolution is
  automatic and immediate on sync, not deferred to the next sweep.
- **One open alert per device**, enforced by a filtered unique index. The
  sweep runs four times a day; re-raising the same fact would make one dead
  tablet produce four rows daily. Resolved alerts are kept and unconstrained —
  which posts keep going dark is the signal behind replacing hardware rather
  than rebooting it.
- **Suspended and revoked devices are never alerted on.** They are meant to be
  silent, and alerting would punish the reporting districts are asked to do.

The alert is raised and visible in the queue; **there is no delivery channel**
— no email or SMS, which would need a provider. A district reads its queue.

## Unauthenticated rate limiting (pre-audit sweep, built)
Certificate verification, the revocation list, the offline bundle and
`/health` are anonymous by design, and anyone can send a bogus token. Each
such request wrote a `RequestLog` row, so an outsider could grow the system of
record's database without bound, and every browser preflight wrote one too.
`UnauthenticatedRateLimiting` limits them per client address
(`UnauthenticatedRateLimit:PermitsPerMinute`, default 120).

- **Only unauthenticated requests are limited.** Authentication now runs
  before the limiter (it refuses nothing, only identifies), and a signed-in
  caller is never throttled: registrars, devices and district nodes share
  addresses behind NAT, and refusing a registration is worse than the flood.
- **The limiter sits before the request audit**, so a refused request writes
  nothing. **CORS moved ahead of the audit too**: it answers preflights itself,
  so they no longer write rows (verified: 8 preflights wrote 8 rows before, 0
  after).
- **`X-Forwarded-For` is ignored unless `TrustedProxies` lists the proxy.**
  Anyone can send the header; trusted by default it would let a caller name a
  new address per request and never be limited. The configured list replaces
  ASP.NET's loopback default. Behind an unlisted proxy every caller shares the
  proxy's allowance — the rejection warning names the address counted, which
  is how that surfaces.
- A 429 carries `Retry-After` and the usual error envelope; the District node
  already treats 429 as "not yet". Tested through a real Kestrel host wired in
  the Api's order, plus live against the Api.

## Audit-log immutability (WS-A2, built)
`AuditLogs` is append-only, enforced by database triggers (the
`AuditLogImmutability` migration) with a matching guard in
`NcbrsDbContext.SaveChanges`.

Nothing in the code ever updated an audit row, so nothing did — but that is
convention, not a control. A corrected audit row is indistinguishable from a
falsified one, and the value of the trail is precisely that it cannot be
rewritten once someone disputes a registration.

- **The trigger is the control; the `SaveChanges` guard is a diagnostic.**
  Anything issuing SQL directly goes around the application layer entirely.
  The guard exists so a developer who writes `audit.Action = …` gets a
  message naming the rule at the line that broke it, and it reports the
  row's **original** values — the half being overwritten is the half that
  matters.
- **Triggers rather than grants in the migration**, because a grant names a
  role only the deployment knows. A production Postgres tier does **both**:
  the triggers, plus `deploy/postgres/app-role-grants.sql`, run as the
  database owner after every migration. The script grants the application
  role the register and revokes `UPDATE`, `DELETE` and `TRUNCATE` on
  `AuditLogs`. They stop different people: a trigger stops anything on the
  connection, and the REVOKE stops anyone holding the app's credentials from
  having the verb at all, or from disabling the trigger, since only the table
  owner can. **So the application must never run as the table owner.** The
  migration emits Postgres triggers too, including a statement-level one for
  `TRUNCATE`, which bypasses row-level triggers entirely.
- **`AuditLogLeastPrivilegeTests` runs the script itself** on CI's Postgres
  job, then acts as the role, asserting `42501 insufficient_privilege`. It
  must check that code specifically: the trigger also refuses an UPDATE, with
  `P0001`, so "the UPDATE failed" would pass with no grants at all. Proven:
  deleting the REVOKE turns the test from 42501 to P0001. The script
  deliberately sets no default privileges. A table added by a later migration
  stays ungranted until the script re-runs, and fails loudly; default
  privileges would grant a recreated `AuditLogs` `UPDATE` silently.
- **Neither stops a database owner**, who can drop a trigger. That is not
  closable here: it is why audit data must also leave the box it is written
  on. WS-A6 puts the WAL archive on its own volume; shipping it off the host
  entirely is the deployment step that finally closes this.
- **Tests migrate rather than `EnsureCreated`.** `EnsureCreated` builds schema
  from the model and never runs a migration's SQL, so the triggers would not
  exist and a test of an absent control would pass for the wrong reason.
- Note SQLite stores these GUIDs as **uppercase** text, so raw SQL built from
  `Guid.ToString()` matches nothing. That trap has now produced a wrong
  result twice in this repo; parameterise instead of interpolating.

## Database providers (WS-A1, built)
`Database:Provider` selects `Postgres` or `Sqlite`, resolved in one place
(`NcbrsDatabase.Configure`) for every host that opens the registry. Postgres
is the central tier (draft 6.4, 7.1); SQLite stays the dev and test provider.

- **Migration histories are per provider and cannot be shared.** EF bakes
  provider-specific column types into a scaffolded migration — SQLite's
  `TEXT` where Postgres wants `text` or `uuid` — so the Postgres set lives in
  `src/NCBRS.Migrations.Postgres` and the SQLite set stays in Core.
  `NcbrsDatabase` names both explicitly, so which history applies is a fact in
  the code rather than a consequence of where a file sits. A migration added
  to one must be added to the other.
- **An unrecognised `Database:Provider` is refused at startup**, not
  defaulted. "Postgresql" silently falling back to SQLite would give a central
  tier a single-file database with no sign anything was wrong.
- **Outside Development the registry is Postgres over verified TLS**
  (`NcbrsDatabase.RefusalOutsideDevelopment`, checked by the Api and Relay at
  startup). SQLite is refused: the base settings used to name it, so a
  deployment that forgot the override would have done exactly what the
  previous bullet guards against, just without a typo. Postgres must use
  `SSL Mode=VerifyFull` or `VerifyCA`: Npgsql's default `Prefer`, and
  `Require`, encrypt without checking the server, so anyone on the path can
  impersonate it. A Unix socket or an all-loopback host list is exempt; one
  remote host in a failover list is not. The SQLite provider and dev
  connection string now live in `appsettings.Development.json`, and the Api
  skips the check under the build-time OpenAPI generator (`GetDocument.Insider`),
  which runs it as Production and never opens the database.
- **Credentials never go in `appsettings.json`.** The connection string comes
  from the environment or a secret store; `Database:Provider` is the only part
  that is configuration.
- **`NCBRS_TEST_PROVIDER=Postgres` runs the whole suite against Postgres.**
  Each test gets its own database, cloned from a migrated template (~80ms)
  rather than re-migrated. The obvious alternative — one database with each
  test in a rolled-back transaction — collides with the transaction EF opens
  for `SaveChanges`, and suppressing that would have quietly broken the tests
  that roll back on purpose.
- **Read a stored date or timestamp with `UtcTime.AsUtc`, never
  `ToUniversalTime()`.** Everything is written as UTC, and SQLite returns it
  with no kind, which `ToUniversalTime()` treats as server-local. A date of
  birth is midnight UTC, so on a server east of UTC it becomes the previous
  day. That signed certificates with the wrong date of birth, and counted a
  day-28 neonatal death as day 29. It had already been fixed locally three
  times (revocation lists, amendment drift, dashboard ranges) and was still
  missed twice; now there is one helper. CI's SQLite job runs in
  `TZ=Africa/Juba` because a UTC runner cannot see this class of bug; the
  suite's older fixtures use times like 04:30, which no shift moves across
  midnight.
- **Postgres timestamps keep microseconds; .NET `DateTime` keeps 100ns
  ticks.** A timestamp compared in memory against the same value read back
  from Postgres will **not** be equal. Nothing in the product does that today,
  but it is the trap to watch for in drift checks and concurrency tokens.
- Two tiers stay on SQLite deliberately: `NCBRS.District` (a mini-PC holding
  one table of batches in transit) and the consumer's read model (rebuildable
  from Kafka by design; the reporting replica is plan F1).
- Editing an already-applied migration does not re-run it — environments that
  ran the old version keep it. Fine while a migration has never left the
  machine; anywhere else, add a new one.

## Backups and recovery (WS-A6, drill done)
Continuous WAL archiving is configured on the compose Postgres, and a restore
drill has been run and timed. The measured RPO/RTO and their caveats live in
`NCBRS-Business-and-Delivery-Plan.md` (§ A6), which is where the plan asks for
them.

Three things worth keeping in mind before changing any of it:

- **Archiving fails silently.** A misconfigured `archive_command` breaks
  nothing a user can see: the database keeps accepting registrations and the
  absence of any recovery point is discovered on the day it is needed. This
  happened here — a named volume mounts root-owned and postgres runs as uid
  999, giving 18 failed archive attempts and zero segments. The compose
  service now chowns the archive directory, and **monitoring must alert on
  `pg_stat_archiver`**; it is the only signal. The Api's anonymous `/health`
  exposes it (`walArchive.failingNow`, and `enabled: false` when archiving is
  off), alongside the outbox backlog, and reports `status: degraded` with the
  reasons (plan §17 19). **It always answers 200.** A 503 would let a load
  balancer pull the API out of rotation over an archive fault while
  registrations work, so alert on `status`, not the code. `failingNow` compares
  the last failure with the last success, because `failed_count` is cumulative
  and would keep paging long after a recovery.
- **The archive is on its own volume**, not beside the data. An archive on the
  same disk as the data it protects is a second copy of the thing that fails.
- **The append-only audit triggers do not obstruct recovery.** `pg_restore
  --clean` drops objects rather than truncating, so a restore over a live
  database works; the triggers and their function are carried in the dump and
  are enforcing again the moment it completes. Worth re-checking if the
  restore procedure ever changes to one that empties tables in place.

A restore is only a recovery if the controls come back with the data — the
drill checks the triggers are enforcing afterwards, not just that rows exist.

## The generated contract (WS-W8)
Both HTTP services generate an OpenAPI document at build time, committed to
`web/openapi/`, and the web client's types are generated from it. CI fails if
either is stale. The generated client is why React was chosen over Blazor, so
a document that misdescribes the service costs that decision its value.

- **Neither document may say a number might be a string.** ASP.NET's web JSON
  defaults set `JsonNumberHandling.AllowReadingFromString`, and the generator
  reports it as `{"type":["integer","string"]}`. One component schema
  describes both directions, so every number a service *returns* inherits it
  and types as `number | string` in the client.
- **The two services close this differently and deliberately — do not unify
  them.** The API has request bodies and chooses to be forgiving about them,
  so it keeps the leniency and corrects the document
  (`NumberSchemaTransformer`). The consumer has **no request bodies at all** —
  every endpoint is a GET — so it is simply not lenient
  (`JsonNumberHandling.Strict`), and its document states what it does rather
  than describing a leniency away. Copying the transformer to the consumer
  would document a behaviour it has no use for.
- **Nullability is not the same widening and must survive.** `["null","number"]`
  is correct and load-bearing: a §10 indicator reports null rather than a
  misleading zero, and that has to reach the contract to reach a dashboard.
  Only the `string` alternative is dropped.
- **`OpenApiNumberTypingTests` pins this over both documents.** The CI drift
  check fails on any contract change, which catches a regression only if
  whoever reads the diff recognises the widening as a fault — it looks like a
  harmless generator detail, and it was rationalised rather than diagnosed
  once already here.
- The generator reads `Microsoft.AspNetCore.Http.Json.JsonOptions`, **not**
  MVC's `Mvc.JsonOptions`. The API configures both through one method so they
  cannot drift; without the former, every enum documents as a bare `integer`.

## Environment notes
- Local dev DB: SQLite by default (`ncbrs.db` at the repo root; the services
  point at it via a relative path). `docker compose up postgres` plus
  `Database__Provider=Postgres` runs the real engine — note it is published on
  **5433**, because a developer machine may already have a local PostgreSQL on
  5432 and the clash presents as "password authentication failed".
- Migrations are applied by the API on startup in Development only. Other
  tiers should run `dotnet ef database update` as a deployment step --
  several services racing to migrate one database will corrupt it.
- `docker-compose.yml` runs Kafka (`apache/kafka`, pinned) and Keycloak with
  an imported realm. Note `bitnami/kafka` no longer resolves; Bitnami moved
  their catalogue.
- Certificate signing refuses to start outside Development without
  `CertificateSigning:PfxPath`, or with `KeyId` left at `ncbrs-dev` (printed in
  every QR, and the throwaway key's id). The dev fallback mints a throwaway key
  whose certificates stop verifying on restart. **"Refuses to start" was not
  true until #125:** the signer is a singleton built on first use, so an Api
  with no key started, reported `/health` ok and 500'd every certificate
  operation, the offline bundle included. `Program` now resolves it right after
  `Build()`, and the TLS rehearsal pins that. A service that checks
  configuration in a lazily-built singleton is checking it late — resolve it at
  startup.
- **`RUNBOOK.md` "Deployment: production configuration checklist"** lists every
  setting each service needs outside Development, with the rehearsal as the
  worked example.
- **Security relaxations live in `appsettings.Development.json`, never the
  base file**, and outside Development the services refuse to start with them.
  The compose Keycloak is plain HTTP, so Development sets
  `Keycloak:RequireHttpsMetadata: false` (API and Consumer); anywhere else that
  setting is refused, because signing keys fetched over HTTP can be swapped by
  anyone on the path, who could then mint accepted tokens. The District node
  likewise refuses an `http://` `Central:BaseUrl` or `Central:TokenEndpoint`
  outside Development — it sends its service-account password and whole
  batches of births over them. A relaxation in the base file is a relaxation
  in production; that is how both of these shipped before. The same goes for
  `CertificateSigning:AllowEphemeralDevelopmentKey`, even though the signer
  also refuses it outside Development.
- **`Keycloak:Authority` must be `https://` outside Development too**, not just
  `RequireHttpsMetadata`. With the flag on and an `http://` authority — the
  shipped default — the Api started cleanly and then failed *every* request,
  anonymous `/health` included, with a 500, because authentication runs on
  every request. The dev authority and the District's dev centre addresses
  now live in the Development files. Every startup check in the Api and
  Consumer is skipped under the build-time OpenAPI generator through one
  `generatingOpenApiDocument` flag, since it runs them as Production.
- **`scripts/tls-rehearsal.sh` rehearses all of this on every pull request**
  (CI job "TLS rehearsal (production config)", ~3 min). Its own CA and a
  private Docker network (`pg.tls`, `kafka.tls`, `keycloak.tls`), the services
  published self-contained for linux-x64 and run in Production: 72 births
  through every link, HTTPS-realm tokens accepted, forged and untrusted ones
  refused and the untrusted case logged, and the #110/#113/#114/#119 refusals.
  **It covers the District tier too:** the load driver (`NCBRS_LOAD_SYNC_BASE`)
  pushes signed batches from an enrolled fleet through a District node in
  Production, which forwards them to the Api's HTTPS listener with a token
  fetched over HTTPS; every batch must be forwarded, none rejected, every
  record registered and no device refused. Dropping the signature header
  (the #100 regression) fails four of those checks; its first run found #123.
  Mutation-checked: disabling #121's logging fails exactly that check. It is
  the only test that runs a service in Production; a change to settings,
  startup checks or connection wiring should expect to meet it. Runs in Git
  Bash too — native Windows `openssl`, `dotnet` and `curl` get Windows paths
  via `hostpath`, because `MSYS_NO_PATHCONV` (needed for Docker) stops Git Bash
  translating them.
- **Rehearsed against real TLS (2026-09-27)**, with throwaway containers and
  the services in Production: Postgres TLS-only with `SSL Mode=VerifyFull`
  (plaintext, a wrong CA and an uncovered hostname each refused; TLS 1.3
  confirmed in `pg_stat_ssl`) and Kafka on SASL_SSL with SCRAM-SHA-512 (a wrong
  password logged as `fail:` and dispatched nothing). 72 births went Postgres →
  Relay → Kafka → Consumer end to end. That run found the authority bug above.
  **Keycloak over HTTPS was rehearsed too**, without touching the host's
  trust store: the Api published self-contained for linux-x64 and run in
  Production in a container that trusts the rehearsal CA via `SSL_CERT_FILE`.
  Tokens from the HTTPS realm were accepted; a token from another realm, a
  forged signature, and every token when the CA was *not* trusted were refused.
- **An unreachable identity provider is logged as an error**
  (`KeycloakReachability`, from `OnAuthenticationFailed` in the Api and
  Consumer). That last rehearsal case refused every signed-in request with 401
  and logged **nothing** at the shipped levels, so a Keycloak outage, a wrong
  authority or an untrusted certificate looked like every user's token being
  bad. On .NET 10 the failed metadata fetch does not surface as itself: the
  handler goes on without configuration and fails with `IDX10204` (no issuer)
  or `IDX10500` (no keys) — the classifier keys on those plus `IDX20803`, and
  a wrong-realm token (`IDX10205`) stays quiet. Once a minute, not per request.
- **Browser origins (`WebClientCors:AllowedOrigins`) are checked at startup by
  the Api and Consumer** (`WebClientCorsOptions.Refusal`). A wildcard is refused
  in every environment: the class said "no wildcard" but nothing checked it,
  and a lone `*` made ASP.NET answer `Access-Control-Allow-Origin: *` to any
  site (verified live). Each entry must be a bare origin, since a trailing
  slash never matches a browser's `Origin` header and fails silently as CORS
  errors. Outside Development only `https://` is accepted. The localhost dev
  origins moved to the Development files; the base files allow no origin,
  which is right for a site served from the same origin as the API.
- **The Kafka link is SASL over TLS by default** (`KafkaOptions.SecurityProtocol`),
  and outside Development the Relay and Consumer refuse to start unless it is
  encrypted **and** authenticated — `SaslSsl` with a username, or `Ssl` with a
  client certificate. Until this there was no way to configure either, and the
  events carry BRNs, coded causes of death and corrected names; an
  unauthenticated broker also lets anyone who reaches it publish into topics
  the projection trusts. Development sets `Plaintext` for the compose broker.
  Both clients are configured by one `KafkaOptions.ApplyTo`. The check is
  options validation on start, skipped for the build-time OpenAPI generator
  (`GetDocument.Insider`), which runs the Consumer as Production. A refused TLS
  handshake or refused credentials logs as an **error**
  (`KafkaLogging.IsConfigurationFault`): librdkafka reports a TLS failure with
  the same code as a broker that is down, which is logged at Debug here, so a
  misconfigured Relay used to look like a quiet outage. The Relay now has a
  launch profile: without one `dotnet run` started it as Production.
