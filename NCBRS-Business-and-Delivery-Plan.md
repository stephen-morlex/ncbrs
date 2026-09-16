# NCBRS — Business & Delivery Plan

Derived from `NCBRS_Draft_v1.3.docx` and verified against this codebase as built.
Durations and phase structure are the draft's indicative figures and remain subject
to ministry validation. No cost figures are estimated here; §7 gives the cost model
to be populated by procurement.

Published version of this document: https://claude.ai/artifact/3ar5XDwtQca9QobSoXWQN5

---

# Part I — Business plan

## 1. Executive summary

NCBRS captures a birth once, at the point of care, and issues a legally valid
certificate that reconciles centrally — without requiring the facility to be online
at the moment of registration.

Birth registration is the foundation of legal identity. It determines access to
healthcare, education, social protection, inheritance, and eventually a national ID
and passport. Today registration is paper-based, siloed per facility, and effectively
unreachable for births in small village clinics or at home.

The central design constraint is connectivity, not software. A system that assumes
reliable internet will not be used where the registration gap is worst. NCBRS is
therefore offline-first.

The programme is materially de-risked on its hardest technical question. The offline
identifier scheme — the thing guaranteeing two disconnected village clinics never
issue the same registration number — is **built, tested and running**, along with the
central API, certificate issuance and signing, duplicate detection, the event
backbone, and offline certificate verification.

| | |
|---|---|
| **Proven** | Central tier — registration, certificates, dedup, events |
| **Largest remaining build** | Tier 1 client — the device app does not yet exist |
| **Longest lead time** | Legal — certificate validity in statute |

Two things now sit on the critical path, independent of each other:

1. **Legal.** A digitally signed certificate has no standing until the Civil
   Registration Act says it does. This runs on legislative time.
2. **The facility and village client.** The tablet application a community health
   worker actually touches.

Neither can be compressed by adding developers late.

> **Recommendation.** Approve Phase 0 and Phase 1 funding only. Commission the legal
> workstream immediately and in parallel with client development, then gate further
> expansion on pilot KPIs — specifically registration completeness and sync
> reliability from village-tier devices.

## 2. The problem, and why now

Five failures compound in the current paper system:

- **Registers are per-facility** and transcribed to district offices by hand, so
  errors enter at transcription and are never caught.
- **Village clinics and community health posts** handle a large share of rural births
  and have the weakest record-keeping — the gap is widest exactly where the system is
  thinnest.
- **No unique identifier** links a birth to later health, education and national ID
  records, producing both duplicates and children never registered at all.
- **Certificates cannot be verified.** A school, bank or passport office has no way to
  distinguish a genuine certificate from a forged one.
- **Vital statistics lag by months** and under-count rural areas, so national health
  planning uses numbers known to be wrong.

What changed is hardware and pattern maturity. Low-cost Android tablets are cheap
enough for nearly every clinic, and offline-first sync is proven in mobile banking and
agricultural extension. Digitising registration no longer requires waiting for
national broadband.

## 3. Service model

### The registration journey

A midwife, nurse or CHW completes a structured birth notification on a local device.
The device issues a permanent-format BRN immediately from a block reserved for it, so
a provisional certificate can be printed and handed over before anyone leaves the
room. The record queues locally; on next connectivity it uploads in one batch and the
centre confirms the number as permanent.

The identifier is the pivot. Because blocks are pre-allocated (the draft proposes 200
at a time), a device offline for three weeks still issues unique, permanent numbers
with no placeholders and no renumbering later — the same approach offline-capable
payment terminals use for transaction ranges.

### Three tiers, one schema

| Tier | Where | Function | Connectivity |
|---|---|---|---|
| **1 — Device** | Hospital terminal, clinic tablet, CHW phone | Capture, local store, number issuance, certificate printing | Intermittent to none |
| **2 — District** | District health office server | Batches and forwards sync; local cache during national outages | Daily or weekly |
| **3 — National** | In-country data centre or sovereign cloud | System of record, reporting, external integrations | Always on |

### Certificate trust

Every certificate carries a unique BRN, a QR code and a digital signature from a
Ministry-controlled key. A signature alone only proves a document was once issued — it
cannot know the register was corrected afterwards. NCBRS therefore publishes a signed
revocation list, and a certificate is valid only when **signed AND not revoked**.

## 4. Governance and ownership

A cross-ministerial Steering Committee — Health, Interior/Justice, National ID
Authority, National Statistics Office — should approve the legal framework and
data-sharing agreements before technical rollout begins. Day-to-day ownership sits
with a Digital Health and Civil Registration Unit inside the Ministry of Health, with
a named Data Protection Officer.

| Stakeholder | Role | Owns the decision on |
|---|---|---|
| Ministry of Health | System owner; funds and operates | Rollout sequence, operating budget |
| Interior / Justice (Civil Registry) | Legal custodian of civil status | Whether NCBRS is the register or its intake channel |
| National ID Authority | Downstream consumer | Integration contract and field set |
| District Health Offices | Registrar oversight, device distribution | Local support model |
| Hospitals & clinics | Primary registration points | — |
| Village posts / CHWs | Registration in low-connectivity areas | — |
| National Statistics Office | Consumer of aggregated statistics | Extract schedule and format |
| Citizens / parents | Beneficiaries | — |

> **Unresolved, and blocking.** Whether the civil registry or NCBRS is the legal system
> of record is still open. It changes the integration pattern, the liability model and
> arguably the data model. Settle it before Phase 1, not during integration.

## 5. Legal critical path

A technically excellent system is worthless if its certificates have no legal
standing. This is the long pole; it cannot be accelerated by the technical team, which
is why it must start first and run in parallel.

1. **Recognise digital certificates in statute.** Amend the Civil Registration Act so
   digitally signed certificates are legally equivalent to paper.
2. **Define registrar liability by tier**, explicitly including CHWs who may not be
   civil servants — today they would create legal records with no defined standing.
3. **Set the statutory registration window** (30/60/90 days) and define late
   registration separately, with additional verification to deter backdating.
4. **Commission a data-residency and data-protection opinion** before the hosting model
   is fixed. This determines cloud vs. on-premises and is not cheaply reversible.
5. **Pre-negotiate data-sharing agreements** with the National ID Authority and the
   civil registry. This becomes the API specification.

> **Engineering dependency.** Item 3 is not only legal. The statutory window is a
> configurable rule the platform must enforce, and the late-registration approval path
> is unbuilt. It has to exist before the first late registration arrives.

## 6. Delivery approach

### Build, not buy

The central platform is already built, Ministry-owned, with no licensing exposure. The
decisive argument against a commercial civil-registration package is the offline tier:
the block-allocation and sync design is specific to this country's connectivity
profile, and is the component least likely to be well served by a product built for
connected environments.

### Team shape

| Function | Responsibility | Phase |
|---|---|---|
| Backend engineers | Central API, district node, integrations | 0 onward |
| Mobile engineers | Tier-1 client, offline store, printing | 0 onward — highest priority |
| Platform / SRE | Hosting, backups, DR, monitoring | From Phase 0 exit |
| Security engineer | Key management, device enrolment, audit | Before pilot |
| Data / statistics | WHO-compliant reporting, DHIS2 and NSO extracts | Phase 2 |
| Training & change | CHW curriculum, district trainers | Phase 1 onward |
| Product / clinical lead | Workflow fit with actual midwifery practice | 0 onward |

### Support model

Peer CHW support first, district IT officer second, central helpdesk last. A small
central team cannot absorb national first-line volume.

## 7. Cost structure

The draft deliberately does not estimate cost, and neither does this plan. What
follows is the cost model — categories and the driver that sizes each one.

| Category | Cost driver | Nature | Relative scale |
|---|---|---|---|
| Village & facility devices | Number of health posts nationally | Capital, phased | Largest line item |
| Training & change management | Number of CHWs, plus per-diems | Operating, front-loaded per region | Large |
| Connectivity | Data plans per CHW device, monthly | Recurring, perpetual | Large over time |
| District tier hardware | Number of district offices | Capital, one-off per district | Moderate |
| Central platform | Hosting, replicas, backup, DR | Recurring | Moderate |
| Development & integration | Team size × duration | Operating, tapers after Phase 3 | Moderate |
| Device maintenance & replacement | Fleet size × failure and refresh rate | Recurring — often omitted | Moderate, compounding |
| Legal & regulatory | Legislative amendment process | One-off, Phase 0 | Small, but gating |
| Security audit & pen testing | Per assessment, repeated | Recurring | Small |

> **Budget warning.** Device fleet economics are routinely underestimated. Tablets in
> rural field use are lost, broken and stolen; a replacement rate applied to the
> national fleet is a permanent recurring line, not a one-off purchase. Connectivity is
> similarly perpetual. Model both over five years before Phase 3 is approved.

## 8. Benefits and how they are measured

| Indicator | Definition | Measurable from |
|---|---|---|
| Registration completeness | % registered within the statutory window, by region | `LateRegistration.DaysLate` — **built** |
| Time to registration | Median days from birth to confirmed BRN, by tier | `DateOfBirth` → `ConfirmedAtUtc` — **built** |
| Sync reliability | % of village devices syncing within X days | `ncbrs.sync.audit` — **built** |
| Duplicate rate | Flagged/confirmed duplicates per 10,000 births | `DuplicateCandidate` — **built** |
| Certificate turnaround | Median registration → certificate in hand | Needs client instrumentation — **partial** |
| Central uptime | Availability of the national tier | Needs monitoring — **gap** |
| District autonomy | Autonomous operation during outages | Needs district tier — **gap** |

Beyond the measurable, the benefit compounds: a unique, verifiable identifier issued
at birth is the foundation later national ID, school enrolment and social protection
systems attach to. Every year without the registry is a cohort needing retrospective
registration later, at higher cost and lower accuracy.

## 9. Risk register

| Risk | Likelihood | Impact | Mitigation | Status |
|---|---|---|---|---|
| Duplicate BRNs from prolonged offline use | Medium | High | Pre-allocated blocks plus central dedup review | **Built** |
| Legal ambiguity delays certificate validity | Medium | High | Phase 0 legal work precedes rollout | Not started |
| Low digital literacy slows CHW adoption | High | Medium | Guided single-purpose form; in-person training | Not started |
| Device loss or theft exposes personal data | Medium | Medium | Encrypted local store, remote de-registration | Not started |
| Unenrolled device submits fraudulent records | Medium | High | Device certificates required before sync | **Mitigated for sync**; the online registration path still takes an unverified deviceId |
| Central outage blocks district sync | Low | Medium | District tier operates semi-autonomously | **Built** |
| Breach of the national registry | Low | Very high | RBAC, encryption, audit log, pen testing | Partial |
| Signing key compromise or expiry | Low | Very high | HSM custody and key rotation | **Rotation built**; HSM custody outstanding (A3) |
| Procurement delays for rural hardware | Medium | Medium | Region-by-region phasing | Programme control |

> **Two risks the original draft under-weighted** (now added in v1.3):
>
> **Device enrolment** was described as closing a major fraud vector while nothing
> enforced it: a device identifier was a free-text string a client asserted, so any
> client holding a valid token could submit records claiming to be any device.
> **Now closed on the sync path** (WS-B9): devices are enrolled against a facility
> and a batch must carry a signature over its raw body made with the device's
> enrolled key. The online registration endpoint still accepts an unverified
> `deviceId` label, so a stolen token can avoid the sync path entirely — closing
> that needs per-request signing or mTLS.
>
> **Signing key rotation** did not appear at all, and the verifier originally accepted
> exactly one key — on the day a new key was issued, every device still holding the old
> one would have rejected genuine certificates. **Now built:** verifiers hold a key set
> and select by the id printed in the QR, and both `/signing-key` and
> `/offline-bundle` publish the whole set. What remains is HSM custody of the private
> key (A3), which is an operational rather than a code change.

## 10. Phasing and funding gates

Durations are the draft's indicative figures. Phase 1 overlaps Phase 0 because client
development does not depend on legislative completion — only pilot *go-live* does.

| Phase | Focus | Indicative duration |
|---|---|---|
| Phase 0 | Foundation & legal | 3–6 months |
| Phase 1 | Pilot district | 4–6 months |
| Phase 2 | Regional rollout | 9–12 months |
| Phase 3 | National rollout | 12–18 months |
| Phase 4 | Interoperability & optimisation | Ongoing |

| Gate | Cannot pass until | Releases funding for |
|---|---|---|
| Phase 0 exit | Legal amendment tabled; data-residency opinion delivered; committee seated; registry-of-record question settled | Pilot deployment |
| Phase 1 exit | A genuinely low-connectivity village post has registered, synced and had certificates verified end to end | Regional rollout |
| Phase 2 exit | Completeness improving against baseline; district tier operating autonomously through a real outage | National rollout |
| Phase 3 exit | National coverage; duplicate rate within tolerance; support absorbing volume | Optimisation and integration |

Pilot district selection matters more than its budget. It must include at least one
hospital, one clinic, and **several genuinely low-connectivity village posts**. A pilot
run only in well-connected facilities validates nothing about the central assumption.

---

# Part II — Development plan

## 11. What exists today

Verified against the running codebase. 262 tests passing.

| Capability | Implementation | Draft ref |
|---|---|---|
| Offline BRN block allocation | Device-issued from reserved block; optimistic concurrency with retry | 6.3, 6.6 |
| Birth registration | `POST /api/BirthRecords/register`, WHO/UN vital-event model | 6.5 |
| Transaction envelope | `{meta, data}` with transaction ID on every request/response, persisted | 4.3 |
| Idempotency | Lease-based with crash recovery and stored response replay | 6.3 |
| BRN confirmation on sync | Centre reconciles the BRN against the blocks it granted; unconfirmable numbers are recorded, audited and left provisional rather than refused | 5.1, 6.3 |
| Batch sync | `POST /api/Sync/batches`; per-record savepoints | 6.3 |
| Per-record attribution | Device-claimed author constrained by facility and PIN existence | 6.7 |
| Authentication & RBAC | Keycloak OIDC, four realm roles, facility scoping | 6.7 |
| Offline device PIN | PBKDF2-HMAC-SHA256, 210,000 iterations, policy-checked | 6.7 |
| Neonatal & maternal outcomes | ICD-PM 28-day and ICD-MM 42-day windows, coded causes | 6.5.1 |
| Certificate issuance | X.509 ECDSA P-256, canonical signed payload, QR < 400 chars | 4.3 |
| Certificate revocation | Signed revocation list with delta fetch; offline verifier answering *unknown* on a stale cache | Extends 4.3 |
| Amendments | Per-field history with previous values, reason, author; withdraws contradicted certificates | 5.3 |
| Central deduplication | Blocking + Levenshtein scoring, twin guard, reviewer queue | 6.6 |
| Transactional outbox | Events commit with the domain write; relay drains to Kafka | 6.4.1 |
| Event backbone | All five topics published, partitioned by district | 6.4.1 |
| Service split | API, relay, consumer and district node deploy independently | 6.4 |
| District tier | Store-and-forward node: holds batches through a central outage, forwards verbatim on recovery, idempotent end to end, holds no copy of the register | 6.2, 7.2 |
| Audit log | Every write attributed to user, device, timestamp, transaction | 4.3, 6.5 |

## 12. Gap analysis

| Requirement | Draft | State | Closed by |
|---|---|---|---|
| Facility / village client application | 6.4, 7.3 | Not started | WS-B |
| Device enrolment & device certificates | 6.7 | **Server side built** — registry, signature over the raw batch, refusal audited | WS-B |
| Encryption at rest on device | 6.7 | Not started | WS-B |
| Statutory window & late registration | 4.1, 5.3 | **Built** | WS-C |
| Amendment approval workflow | 5.3 | **Built** — two-track | WS-C |
| Conflict handling on concurrent amendment | 6.3 | **Built** | WS-C |
| BRN block exhaustion fallback | 6.3 | **Built** | WS-C |
| MaternalStatistics capture | 6.5 | **Built** | WS-C |
| District / regional tier | 6.2, 7.2 | **Built** | WS-D |
| Production database (PostgreSQL) | 6.4 | **Built** — provider behind config, per-provider migration histories, suite green on both | WS-A |
| Backups & tested disaster recovery | 7.1 | **Drill completed and timed**; WAL archiving on, PITR replay not yet rehearsed | WS-A |
| Audit log immutability enforced | 4.3 | **Built** — database triggers; off-box append-only storage still needed (A6) | WS-A |
| Signing key custody & rotation | 6.4, 6.7 | **Built** — multi-key verification | WS-A |
| Idempotent consumers before read models | 6.4.1 | **Built** — facts keyed by BRN, held out-of-order events | WS-A |
| Security audit & penetration test | 9 | Not started | WS-A |
| National ID Authority push | 6.8 | Not started | WS-E |
| Civil registry two-way API | 6.8 | Not started | WS-E |
| DHIS2 aggregate export | 6.8 | **Built** — district-month aggregates with small-cell suppression | WS-E |
| Statistics office extract | 6.8 | Not started | WS-E |
| Ministry dashboards & reporting replica | 6.4 | **Read models and indicator queries built**; no UI, no replica | WS-F |
| Monitoring, alerting, runbooks | 10 | Not started | WS-G |
| Remote device de-registration | 9 | Not started | WS-G |
| District Wi-Fi / USB / SMS fallbacks | 6.3, 7.4 | Not started | WS-H |
| Annulment of a record registered in error | — | **Built** | WS-C |

## 13. Decisions confirmed in draft v1.3

| Draft v1.2 said | Built as | Resolution |
|---|---|---|
| .NET 8 LTS | .NET 10 | **Accepted.** Also LTS, longer support horizon. Draft updated. |
| ASP.NET Core Identity | Keycloak (OIDC) | **Accepted.** Manages users *and* clients; realm roles and client registration come as standard. Draft updated. |
| Corrections follow a registrar-approval workflow | Two-track: clinical measurements apply at once, identity fields await approval | **Settled and built.** The immediate track is birth weight, gestational age and birth order — the fields describing the event. The approval track is the child's name, date of birth and sex, plus both parents' names: everything describing who the record is about. A submission may split across both tracks; a wholly pending one answers 202. Approval is refused to the submitter. |
| Certificates verified by signature | Signature *and* revocation list | **Accepted and added to draft §4.3.** A gap in the draft, not a deviation from it. |

## 14. Workstreams

Eight parallel tracks. Steps within a track are ordered and carry an exit condition.

### WS-A — Production hardening of the central tier

*Why first:* the pilot writes real legal records. *Depends on:* data-residency opinion
for A1's hosting target.

| # | Step | Exit condition |
|---|---|---|
| A1 | Migrate to PostgreSQL (Npgsql behind config; keep SQLite for dev) — **done** | Full test suite green against Postgres |
| A2 | Enforce audit-log immutability at the database (triggers now; REVOKE UPDATE/DELETE alongside them once A1 lands) — **done** | Application role provably cannot alter a written audit row |
| A3 | Move the signing key to an HSM or secret store | Production refuses to start without a real key |
| A4 | Support key rotation (verifier accepts a key set; bundle carries several) — **done** | Cert signed by key B verifies on a device holding A and B |
| A5 | Make consumers idempotent, **then** build read models — **done** | Replaying a partition leaves totals unchanged |
| A6 | Backups and a tested restore — **drill done; PITR replay drill outstanding** | Restore drill completed and timed; RPO/RTO recorded (see below) |
| A7 | Load and soak testing (burst shape, not average) | Sustained concurrent batch syncs at projected volume |
| A8 | Independent security audit and penetration test | Findings remediated or accepted by the DPO |

#### A6 — recovery objectives, as measured

A drill was run against the Postgres central tier: back up, destroy the registry
outright, restore, and confirm the system still works.

| | Measured | Notes |
|---|---|---|
| **RPO** | **≤ 60s** | `archive_timeout=60` forces a WAL segment even when the registry is idle overnight. Without continuous archiving the RPO would be the dump interval — up to a day of births. |
| **RTO** | **291 ms** | `createdb` + `pg_restore` from total loss. |
| Logical dump | 75 ms / 64 KB | `pg_dump --format=custom`. |
| Restore over a live database | 320 ms | `pg_restore --clean`; does **not** trip the append-only audit triggers, because `--clean` drops rather than truncates. |
| Base backup | 1.77 s / 6.4 MB | `pg_basebackup`; Postgres confirmed every required WAL segment was archived. |

**These timings are a floor, not a commitment.** They were taken at pilot data
volume; `pg_restore` time is dominated by row count and index rebuild, so the
RTO must be re-measured at projected national volume under A7. Quoting 291 ms
to a Steering Committee as the national recovery time would be false comfort.

What the drill proved beyond the timings: the restored database is *functional*
— a pre-loss record read back correctly, a new birth registered against it, and
the append-only audit triggers were still enforcing after recovery. A restore
that returns rows but loses the controls protecting them is not a recovery.

**Still outstanding:** the PITR replay itself (restore a base backup, replay WAL
to a chosen instant) has not been rehearsed. Archiving is verified and the chain
is intact, so the recovery point exists; what is unproven is the procedure for
using it under pressure, which needs a staging instance.

### WS-B — Facility and village client

*Why it is the long pole:* the entire Tier-1 experience, none of which exists.
*Depends on:* nothing — start immediately.

| # | Step | Exit condition |
|---|---|---|
| B1 | Decide .NET MAUI vs PWA (recommend MAUI; printing is a hard requirement) | Decision recorded with printing/storage evidence |
| B2 | Local encrypted SQLite store mirroring Core schema | Database file unreadable without the device key |
| B3 | Offline PIN unlock against cached credential bundle | Registrar unlocks with no connectivity; wrong PIN rate-limited locally |
| B4 | Guided registration form, designed with actual midwives/CHWs | Untrained CHW completes a registration unaided |
| B5 | BRN block consumption and low-block warning | Simulated three-week offline period, no collision |
| B6 | Local outbox and batch sync | Partially rejected batch leaves exactly the rejected records queued |
| B7 | Provisional certificate printing with QR | Printed certificate scans and verifies on a second device |
| B8 | Offline verification + bundle refresh each connectivity window | A revoked certificate is refused with no network |
| B9 | Device enrolment (device certificate required for sync) — **server side done**; device-side key handling awaits the client | Valid user token from an unenrolled device is refused |

> Without B8's scheduled refresh the cache expires and the device correctly but
> uselessly answers *unknown* to everything.

### WS-C — Registration rules still missing

*Why it matters:* these are legal behaviours, not features. *Depends on:* the statutory
window chosen in law.

| # | Step | Exit condition |
|---|---|---|
| C1 | Statutory registration window — **done** | A record past the window is flagged, not silently accepted |
| C2 | Late-registration approval with district sign-off — **done** | No certificate issues until a district registrar other than the filer verifies the evidence |
| C3 | Two-track amendment approval (identity fields queue; clinical measurements apply) — **done** | Name or parent change queues; birth-weight correction does not |
| C4 | Annulment of a record registered in error — **done** | Every acting path refuses; excluded from duplicate matching; nothing deleted |
| C5 | Concurrent amendment conflict handling — **done** | Collision produces a review item, not a silent overwrite; detection is per field so the queue stays signal |
| C6 | BRN block exhaustion fallback (flagged local sequence) — **done** | Exhausted device keeps registering; every fallback arrives flagged and is reconciled to a real BRN on sync |
| C7 | MaternalStatistics capture endpoint and form — **done** | Statistical variables recorded, coded to ISCED/ISCO, without appearing on the certificate |

### WS-D — District tier

*Why deferrable:* the pilot can sync devices directly to the centre. *Depends on:* WS-A;
not needed for Phase 1.

| # | Step | Exit condition |
|---|---|---|
| D1 | District node service on low-spec hardware — **done** | Node accepts a batch and forwards it intact |
| D2 | Store and forward with end-to-end idempotency — **done** | Replayed forward produces zero duplicates |
| D3 | Autonomous operation during central outage — **done** | Registration continues through a simulated outage |
| D4 | Reconciliation on recovery — **done** | Post-outage totals reconcile exactly |

### WS-E — Interoperability

*Why it waits:* gated on data-sharing agreements, not engineering. The event backbone
already exists.

| # | Step | Exit condition |
|---|---|---|
| E1 | National ID push from `ncbrs.birth-records.registered` | Confirmed registration appears in the Authority's test environment |
| E2 | Amendment propagation from `ncbrs.birth-records.amended` | Correction reaches the downstream copy within the agreed window |
| E3 | Civil registry two-way API | Round-trip agreed field set, both directions |
| E4 | DHIS2 aggregate export (anonymised, aggregate only) — **done** | Export contains no record-level identifiers |
| E5 | Statistics office scheduled extract | NSO accepts a full reporting-period extract |

### WS-F — Reporting and dashboards

*Depends on:* **A5 strictly.**

| # | Step | Exit condition |
|---|---|---|
| F1 | Reporting replica | Dashboard load leaves registration latency unchanged |
| F2 | Read models from the event stream — **done** | Read-model totals reconcile against the register |
| F3 | Ministry dashboard with district drill-down — **query layer done, UI not started** | Every §10 KPI visible without a manual query (§8 in v1.2 numbering; three of the six are not derivable from the event stream — see the `NotAvailable` list the summary returns) |
| F4 | Devices that have stopped reporting — **done**; alerts are raised to a district queue, with no email/SMS delivery channel | Device silent beyond threshold raises an alert to its district |

> A silent device is indistinguishable from a district with no births, and only one of
> those needs intervention.

### WS-G — Operations, support and training

*Depends on:* WS-B far enough along to train against a real client.

| # | Step | Exit condition |
|---|---|---|
| G1 | Monitoring, alerting, runbooks | On-call engineer can diagnose a stalled relay from the runbook alone |
| G2 | Device fleet management and remote de-registration | Reported-stolen device cannot sync within one connectivity window |
| G3 | Tiered support model | Pilot escalation data shows most issues resolved below the centre |
| G4 | Training curriculum (workflow, not system) | District trainer delivers the course without central staff |

### WS-H — Connectivity fallbacks

*Why last:* each tier is only worth building once the pilot shows which posts the
primary path actually fails for.

| # | Step | Exit condition |
|---|---|---|
| H1 | Sync-point workflow (district office / connected clinic) | Full outbox drains within a typical visit |
| H2 | USB / offline file transfer, signed | A tampered transfer file is rejected |
| H3 | SMS minimal-subset confirmation | SMS-registered birth reconciles against its later full record |

## 15. Sequencing

Only three hard dependencies exist across the eight tracks:

- **A5 before F2.** Consumers must be idempotent before any writes a read model, or a
  replayed partition silently double-counts a district's births.
- **Legal §5 item 3 before C1.** The statutory window is a number set in law.
- **B4 before G4.** Training material cannot be written against a form that does not
  exist.

| Phase | Tracks active | Gate |
|---|---|---|
| Phase 0 — Foundation | Legal · WS-A · WS-B (starts) · WS-C | Legal tabled; platform production-ready |
| Phase 1 — Pilot | WS-B (completes) · WS-F · WS-G | One low-connectivity post registers, syncs and verifies end to end |
| Phase 2 — Regional | WS-D · WS-G (scale) · WS-H | District tier survives a real outage |
| Phase 3 — National | WS-G · WS-H | National coverage; support absorbing volume |
| Phase 4 — Interoperability | WS-E · WS-F (maturity) | Ongoing |

> **The scheduling trap.** WS-B is the only track that cannot absorb a late staffing
> decision. Mobile engineers added in month five will not recover a start delayed from
> month one, because the field research behind B4 has its own lead time and cannot be
> parallelised. If one hiring decision is made immediately, make it this one.

## 16. Next 90 days

1. **Convene the Steering Committee** and commission the legal amendment. Longest lead
   time in the programme; every week of delay moves the pilot date one for one.
2. **Settle the system-of-record question** — NCBRS or the civil registry. Shapes WS-E
   entirely; cheap now, expensive during integration.
3. **Commission the data-residency opinion.** Hosting cannot be procured until it lands.
4. **Hire or assign mobile engineers.** WS-B is the critical path.
5. **Select the pilot district** — a hospital, a clinic, and several genuinely
   low-connectivity village posts. A convenient district instead of a representative
   one invalidates the pilot.
6. **Open informal talks with the National ID Authority.** The data-sharing agreement
   will take longer to negotiate than the integration takes to build.
