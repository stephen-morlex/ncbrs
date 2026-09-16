# NCBRS — National Civil Birth Registration System

## What this is
An ASP.NET Core (.NET 10) Web API for a Ministry of Health birth
registration system, designed to work across hospitals, clinics, and
offline-first village health posts.

Read these first — they explain *why* the architecture looks like this, not
just what it does:
- `NCBRS_Draft_v1.3.docx` — the current policy/legal/technical draft.
  v1.3 reconciles the draft with the platform as built; `NCBRS_Draft.docx`
  is the superseded v1.2, kept for reference. Cite v1.3 section numbers.
- `NCBRS-Business-and-Delivery-Plan.md` — the business case plus the
  sequenced build plan (workstreams WS-A…WS-H). **Its gap analysis (§12) and
  sequencing (§15) are the plan of record for what to build next** — prefer
  it over the "Not yet done" list below, which is only a summary.
- `NCBRS-Web-Plan.md` — the central management web front end: its plan, the
  backend gaps it forces (search, facilities, registrars, audit, CORS,
  pagination) and the phased to-do list.
- `CONTRIBUTING.md` — the delivery workflow: branch, test, review, commit,
  PR, merge, and the approval gates. CLAUDE.md carries the design rules;
  CONTRIBUTING carries the process ones.

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
  centre validates, and the node is genuinely the uploader.
- **"The centre said no" ≠ "the centre did not answer".** Only 4xx (except
  408/429) stops retrying; everything else stays queued. Conflating them
  would discard births whenever a link drops.
- **Idempotency is end to end** via the caller's transaction id, carried
  through unchanged. Proven live: the centre received one transaction twice —
  once forwarded, once direct from the device — and produced one batch and
  two records.
- **No authentication of its own.** The centre authenticates every batch
  properly, and a second identity system on a district box would add a place
  credentials live without adding a check. Device enrolment (WS-B9) is what
  should gate this hop, and does not exist yet.

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
Summary only — `NCBRS-Business-and-Delivery-Plan.md` §12 is the full list.
The largest gap by far is the Tier-1 facility/village client (WS-B), which
does not exist at all and is the programme's critical path.
- **(WS-B8)** Offline verification exists as a library (`NCBRS.Certificates`,
  `GET /api/certificates/offline-bundle`) but nothing calls it on a schedule.
  The facility device app is not in this repo; when it is written it must
  refetch the bundle whenever it has connectivity, alongside its BRN block,
  or its cache expires and it starts answering Unknown.

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

`BirthRegisteredEvent` carries `FacilityTier`, `VitalEventType`,
`WithinStatutoryWindow` and `ConfirmedAtUtc` for these indicators. **All four
are nullable on purpose**: events already in the topic predate them, and a
consumer must be able to tell "this birth was on time" from "this event
cannot say". `WithinStatutoryWindow` carries the *decision* rather than the
timestamps because the window is set in law — recomputing it downstream next
year would silently restate what was on time last year.

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
   Timeliness is a decomposition too, for the same reason.
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
- **An unmapped district is reported, not dropped.** Its births would
  otherwise never reach the national figures and nothing would say so.
- **Suppressions are returned with the export**, not logged quietly. A
  recipient who cannot tell a withheld figure from an absent one reads the
  gap as zero, and "zero stillbirths" is a materially different claim from
  "too few to publish safely".
- Annulled registrations are excluded — sending one would report a birth the
  register has withdrawn.

Not closed: differencing across periods (cumulative figures published month
after month can narrow a suppressed cell) and the cross-tabulation risk if
more breakdowns are added later. Both need review before the export goes to
an external recipient on a schedule.

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
  device app exists yet to hold a key.
- **The signature covers the raw request body, byte for byte** — not a
  canonical projection of its fields. A canonical form is a second
  description of the payload, and the day it disagrees with the parser a
  genuine batch fails or a tampered one passes. This also survives the
  district tier, which stores and forwards a batch's raw text unchanged; both
  designs come from the same rule, that an intermediary must not need to
  understand a batch to carry it. **This is the gate the District section
  says was missing.**
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
  interesting thing the endpoint sees.
- **`LastSeenAtUtc` is null until a device first syncs**, which is what makes
  a deployment that failed silently visible — previously a post that never
  reported looked like a quiet area. This is what device silence alerts
  (below) are built on.

**Residual, not closed by this:** the enrolment check covers sync batches, as
the plan scopes it. `POST /api/birthrecords/register` still takes an
unverified `deviceId` label, so a stolen token can use the online path
without touching enrolment. Closing that needs per-request signing or mTLS.
Also note `NcbrsRoles.CrossFacility` is not district-scoped anywhere in the
system, so a district officer may enrol for any facility.

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
- **Triggers rather than grants**, because a grant names a role only the
  deployment knows. A production Postgres tier should do **both**: these
  triggers plus `REVOKE UPDATE, DELETE ON "AuditLogs"` from the application
  role. They stop different people — a trigger stops anything on the
  connection, a REVOKE stops anyone holding the app's credentials from having
  the verb at all. The migration emits Postgres triggers too, including a
  statement-level one for `TRUNCATE`, which bypasses row-level triggers
  entirely.
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
- **Credentials never go in `appsettings.json`.** The connection string comes
  from the environment or a secret store; `Database:Provider` is the only part
  that is configuration.
- **`NCBRS_TEST_PROVIDER=Postgres` runs the whole suite against Postgres.**
  Each test gets its own database, cloned from a migrated template (~80ms)
  rather than re-migrated. The obvious alternative — one database with each
  test in a rolled-back transaction — collides with the transaction EF opens
  for `SaveChanges`, and suppressing that would have quietly broken the tests
  that roll back on purpose.
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
  `pg_stat_archiver.failed_count`**; it is the only signal.
- **The archive is on its own volume**, not beside the data. An archive on the
  same disk as the data it protects is a second copy of the thing that fails.
- **The append-only audit triggers do not obstruct recovery.** `pg_restore
  --clean` drops objects rather than truncating, so a restore over a live
  database works; the triggers and their function are carried in the dump and
  are enforcing again the moment it completes. Worth re-checking if the
  restore procedure ever changes to one that empties tables in place.

A restore is only a recovery if the controls come back with the data — the
drill checks the triggers are enforcing afterwards, not just that rows exist.

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
  `CertificateSigning:PfxPath`. The dev fallback mints a throwaway key whose
  certificates stop verifying on restart.
