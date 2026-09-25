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
| Facility / village client application | 6.4, 7.3 | **Client-core built & tested** (BRN allocation, outbox/sync, offline verification, PIN, signing, composed by `FacilityClient`); MAUI shell scaffolded | WS-B |
| Device enrolment & device certificates | 6.7 | **Both sides built** — server registry + raw-batch signature; device-side `DeviceSigner` over the shared `DeviceSignature` | WS-B |
| Encryption at rest on device | 6.7 | Not started (device shell) | WS-B |
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
| Ministry dashboards & reporting replica | 6.4 | **Read models, indicator queries and dashboard UI built** (`web/`); reporting replica (F1) still needed | WS-F |
| Monitoring, alerting, runbooks | 10 | **Runbook built** (`RUNBOOK.md`); live monitoring/alerting needs infra | WS-G |
| Remote device de-registration | 9 | **Built** — suspend/reinstate/revoke (API + web); a non-`Enrolled` device is refused at sync | WS-G |
| District Wi-Fi / USB / SMS fallbacks | 6.3, 7.4 | **Signed USB transfer built** (H2, client-core); sync-point workflow (H1) and SMS (H3) not started | WS-H |
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
| A7 | Load and soak testing (burst shape, not average) — **driver built; national-volume run outstanding** | Sustained concurrent batch syncs at projected volume |
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

#### A7 — the driver, and what still owes a number

The load-and-soak driver exists (`tools/NCBRS.LoadTest`, its README). A7 is a
*burst* problem, not an average one — the load that matters is many village
posts uploading weeks-long outboxes at once when a region reconnects — so the
driver fires whole sync batches concurrently against the real
`POST /api/sync/batches` and reports the latency **shape** (p50…p99, max), which
is where a thundering herd shows up and a mean does not.

The driver has now been run against the **Postgres** tier (dev hardware, WAL
archiving on, all devices at one facility — a conservative floor, not production):

| Run | Result |
|---|---|
| Concurrency 1, batches of 25 | ~6.6 records/s → **~128 ms per record** |
| Concurrency 16, 32-device fleet, 5000 records | **~32 records/s, 0 failures**; p50 12.2s / p99 16.4s per 25-record batch |
| Concurrency 16, **single** device | throughput collapses, ~55/200 batches time out — every batch contends on one `Device` row's last-seen update |

**Superseded: the per-record figures above are inflated** by the driver's own
test data (§17 11d). Its synthetic names flagged almost every same-day pair as
a duplicate, so each run measured a flood of `DuplicateCandidates` writes that
real registrations don't produce. Re-measured 2026-09-25 with the driver fixed.
Same hardware and same day, each run on its **own fresh database**, signing
enforced, a 32-device fleet, batches of 25. The only difference between the
columns is the driver:

| Run | Old driver | Fixed driver |
|---|---|---|
| Duplicate candidates raised (13,071 records) | **166,961** | **0** from load (5 planted in the dev seed) |
| Concurrency 16, 10,000 records | 131.6 records/s; p50 3.1 s / p99 4.6 s per batch | **295.2 records/s**; p50 1.3 s / p99 1.9 s |
| Concurrency 1, 2,500 records | 11.9 records/s → 84 ms per record | **38.8 records/s → 26 ms per record** |

So the clean per-record floor on this hardware is about 26 ms, not 120 ms, and a
burst of 16 concurrent uploads sustains about 295 records/s, with zero failures
in both runs.

Two things this settled. First, the write path is **not globally serialised** —
concurrency scales throughput ~5× (1→16) once the load is spread across distinct
devices, as a real burst is. The single-device collapse was a *test* artifact
(one row, many writers), which is why the driver now enrols a device fleet.
Second, the **~120 ms per-record floor** (since shown to be inflated by the
driver's own test data; the clean figure is ~26 ms, see the re-measurement
above) was chased to its actual cause, which
was **not** the duplicate-detection scan as first assumed: `EXPLAIN ANALYZE`
shows that query uses `IX_BirthRecords_DateOfBirth` and runs in ~5 ms even over a
dense window. The floor is the **per-record write path**: a `SaveChanges` and a
savepoint per record (so one bad row costs only itself), the append-only audit
triggers and the outbox insert. It is *not* a commit per record. A device's
transaction id puts the whole batch in one transaction, which commits, and
fsyncs, once (see `docs/adr/0001-sync-commit-granularity.md`). The one real
duplicate-scan cost — it change-tracked its ~900 read-only candidates per record,
bloating the change tracker across a batch — is fixed with `AsNoTracking`
(p90/p99 roughly halved, throughput +24% at concurrency 1). The residual floor is
the per-record write path, a deliberate per-record-isolation choice rather than a
defect.

What the tool still does *not* provide is the A7 exit condition itself: this same
run on **production-grade Postgres**, with the fleet spread across **multiple
facilities** and volumes sized to a real region's reconnect burst — and, per §A6,
the restore RTO re-measured at that volume while the data is present. (On the
**SQLite** dev provider the same driver shows the opposite curve — throughput
*falls* as concurrency rises, the single-writer signature — which is exactly why
A7 is defined against Postgres.)

What the drill proved beyond the timings: the restored database is *functional*
— a pre-loss record read back correctly, a new birth registered against it, and
the append-only audit triggers were still enforcing after recovery. A restore
that returns rows but loses the controls protecting them is not a recovery.

**Still outstanding:** the PITR replay itself (restore a base backup, replay WAL
to a chosen instant) has not been rehearsed. Archiving is verified and the chain
is intact, so the recovery point exists; what is unproven is the procedure for
using it under pressure, which needs a staging instance.

### WS-B — Facility and village client

*Why it is the long pole:* the entire Tier-1 experience.
*Depends on:* nothing — start immediately.

**Status: the offline-first client-core is built, tested (34 tests) and
composed** — `client/NCBRS.Client.Core`, driven by the workflow facade
`FacilityClient` and exercised end to end by `client/NCBRS.Client.Harness`
(enrol → unlock → register incl. block exhaustion → signed upload → transfer →
settle → offline verify). A **MAUI shell is scaffolded** at
`client/NCBRS.Client.App` (wired to the core, kept out of `NCBRS.slnx` since CI
has no MAUI workload). What remains needs a **device environment**: the screens,
the encrypted store and printing. See `client/INTEGRATION.md`.

| # | Step | Exit condition |
|---|---|---|
| B1 | Decide .NET MAUI vs PWA — **done** (MAUI: printing, encrypted storage, weeks-offline) | Decision recorded with printing/storage evidence |
| B2 | Local encrypted SQLite store mirroring Core schema — **shell scaffolded; store awaits device build** | Database file unreadable without the device key |
| B3 | Offline PIN unlock against cached credential bundle — **logic done** (`OfflinePinLock`, rate-limited); unlock screen in the shell | Registrar unlocks with no connectivity; wrong PIN rate-limited locally |
| B4 | Guided registration form, designed with actual midwives/CHWs — **not started** (needs field research) | Untrained CHW completes a registration unaided |
| B5 | BRN block consumption and low-block warning — **done** (`DeviceBrnAllocator`) | Simulated three-week offline period, no collision |
| B6 | Local outbox and batch sync — **done** (`SyncOutbox`) | Partially rejected batch leaves exactly the rejected records queued |
| B7 | Provisional certificate printing with QR — **not started** (device/printer) | Printed certificate scans and verifies on a second device |
| B8 | Offline verification + bundle refresh each connectivity window — **done** (`CachedVerificationBundle`) | A revoked certificate is refused with no network |
| B9 | Device enrolment (device certificate required for sync) — **done both sides** (server enrolment + device-side `DeviceSigner` over the shared `DeviceSignature`) | Valid user token from an unenrolled device is refused |

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
| F3 | Ministry dashboard with district drill-down — **built** (`web/`: charted summary, county drill-down, per-tier time-to-registration, null-vs-zero, `stillFilling`, `notAvailable[]`) | Every §10 KPI visible without a manual query (§8 in v1.2 numbering; three of the six are not derivable from the event stream — see the `NotAvailable` list the summary returns) |
| F4 | Devices that have stopped reporting — **done**; alerts are raised to a district queue, with no email/SMS delivery channel | Device silent beyond threshold raises an alert to its district |

> A silent device is indistinguishable from a district with no births, and only one of
> those needs intervention.

### WS-G — Operations, support and training

*Depends on:* WS-B far enough along to train against a real client.

| # | Step | Exit condition |
|---|---|---|
| G1 | Monitoring, alerting, runbooks — **runbook done (`RUNBOOK.md`); live monitoring/alerting still needs infra** | On-call engineer can diagnose a stalled relay from the runbook alone |
| G2 | Device fleet management and remote de-registration — **built (API + web)**: enrol/suspend/reinstate/revoke with a reason, and a non-`Enrolled` device is refused at sync even with enforcement off | Reported-stolen device cannot sync within one connectivity window |
| G3 | Tiered support model | Pilot escalation data shows most issues resolved below the centre |
| G4 | Training curriculum (workflow, not system) | District trainer delivers the course without central staff |

### WS-H — Connectivity fallbacks

*Why last:* each tier is only worth building once the pilot shows which posts the
primary path actually fails for.

| # | Step | Exit condition |
|---|---|---|
| H1 | Sync-point workflow (district office / connected clinic) | Full outbox drains within a typical visit |
| H2 | USB / offline file transfer, signed — **done** (`OfflineTransferFile` in `client/NCBRS.Client.Core`: the device packs the batch with its signature; opening verifies it against the enrolled key before anything is forwarded. The sync-point app that reads the media and forwards the body is H1) | A tampered transfer file is rejected |
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

## 17. Remaining work — consolidated to-do

*As of 2026-09-24, reviewed against the code rather than carried forward from
earlier notes.* §12 and §14 remain the per-item record; this section orders what
is left by **what unblocks it**, because almost nothing remaining is blocked on
engineering effort — it is blocked on hardware, agreements, infrastructure,
people or a decision. The central tier is essentially feature-complete; the
critical path is still WS-B's device build.

### A. In the repo, no external blocker

| # | Task | Status | Done when |
|---|---|---|---|
| 1 | Refresh CLAUDE.md's administrative-geography section — it still described the district→county rename as deferred | **Done** | Instructions match the code |
| 2 | Credit H2 in this plan (signed transfer file was built but unmarked) | **Done** | §12 and §14 show it |
| 3 | Scope district officers to their own county. Wider than recorded: writes were national for oversight roles while reads were county-scoped, across all sixteen write paths (amendments, late registration, certificates, outcomes, sync, devices, BRN blocks), and alert acknowledgement had no facility check at all | **Done** | Refused outside their county, fail-closed on an unresolvable county; ministry admin stays national |
| 3a | Scope the two remaining **read** paths: the device list (no facility filter) and the alert queue still return every county to a district officer | **Done** | Both follow `CountyScopeResolver`: narrowed to own county by default, another county named is refused |
| 4 | Signature mode in the A7 load driver, so load tests run the production `RequireSignature: true` path (#91; verified against an enforcing server — signed 12/12 accepted, unsigned 12/12 refused) | **Done** | Driver signs batches and runs clean with enforcement on |
| 5 | Dev environment hygiene: the compose Postgres held a stale pre-SS seed binding (`nurse.lado` → the old "Kabwe" facility), ~11k synthetic load births and `LOADTEST-` devices. `scripts/dev-reset.ps1` (#92) — backed up, clears Kafka too; run for real, `nurse.lado` now bound to Juba | **Done** | A documented reset restores a clean seed |
| 6 | Lint and build warnings (#90). Zero warnings on both stacks; `react` lint gated, `rules-of-hooks` as an error. One warning was a real bug: a retried registration drew its next BRN with the token from before a renewal | **Done** | The `react` oxlint plugin can be enabled under `--deny-warnings` |

### B. Needs a decision

| # | Task | Decision |
|---|---|---|
| 7 | End-to-end web tests (register, amend + approve, issue, annul) | **Done** — Playwright adopted; all four paths green locally and in CI |
| 8 | Localisation scaffolding | **Done** — i18next adopted; shell translated, screens converted incrementally |
| 9 | Close the unverified `deviceId` on online `POST /register` — a stolen token bypasses enrolment there | **Done** — per-request signing; channel decided by the token's `azp`; also fixed refusal audits being rolled back for real devices |
| 9a | The same trust on other endpoints that accept a `deviceId` as an attribution label (BRN block request, certificate issue) | **Done** — same channel rule on all seven (correction, BRN block, certificate issue and reprint, maternal statistics, both outcomes) through one `DeviceChannelGate`; the label is the audit trail, so it was not "just a label" |
| 10 | WCAG 2.2 AA conformance (contrast, 2.5.8 target size, assistive-technology testing) and breakpoint verification | **Machine-checkable part done** — `web/e2e/accessibility.spec.ts` scans all 20 pages signed in, at desktop and phone width, against WCAG 2.2 AA with axe-core in CI. The first scan found three real faults, all fixed: `--muted-foreground` (4.34:1) and `--destructive` (3.99:1 on its tint) under 4.5:1; tables that scroll sideways on a phone but could not be reached by keyboard; and record details in a list with no list items. **Still needs a person:** testing with real assistive technology (screen reader, keyboard-only, zoom), which no rule engine can do |
| 11 | Per-record vs per-batch `SaveChanges` in sync — keep per-record for failure isolation unless real-scale measurement shows round-trips dominate | **Done** — [ADR 0001](docs/adr/0001-sync-commit-granularity.md): keep each record flushed in its own savepoint, inside one transaction per batch. It was never a commit per record: a device's transaction id already makes the batch one transaction. Flushing once per batch would lose in-batch duplicate detection, not just isolation |
| 11a | **Reproduced (2026-09-25).** A forward that outlasts the District's 20 s timeout is cancelled at the centre, which rolls back the **whole** batch. The District then retries it into the same timeout forever (backoff capped at 15 min, no attempt limit), reporting "Queued" with an error that reads like a link fault, while no birth reaches the centre. Under concurrent sync load a 500-record batch timed out at 20 s, and the same batch sent directly took 46.7 s. On an idle register it took 6–10 s | **Done** — each attempt gets `Timeout + TimeoutPerRecord × records` (2 min for 500), doubled per consecutive timeout, capped at 10 min. A timeout is reported as slowness (`slowToFinish`), not an outage, and holds the queue in order. The inline forward no longer dies with the device's connection. The store is upgraded in place |
| 11b | **Found and reproduced while testing 11a: the District forwards a new batch twice at once.** The controller saves it as `Queued` with no next-attempt time and forwards it inline, and the poller sees the same row as due and forwards it concurrently. The centre registers the batch once (the idempotency key holds), the second forward gets `409`, and whichever District save lands last wins. Observed: the device was told `200 Forwarded`, all 60 records were registered, and the District then reported the batch **`Rejected`** and overwrote the stored central response | **Done** — every forward claims its row before sending (the controller in the same write that stores it). The centre's in-progress `409` carries `Retry-After`, and a 4xx with `Retry-After` is "not yet" |
| 11c | **Found and reproduced: device signatures do not survive the District tier.** The node stores and forwards the raw body but never the `X-NCBRS-Device-Signature` header. With `RequireSignature: true`, the production default, every forwarded batch is refused `403`, audited `DeviceRefused:SignatureFailed`, and marked `Rejected`. A correctly signed batch sent directly was accepted. **The District tier cannot deliver anything under the default configuration** | **Done** — the body is read as raw bytes and the signature header is stored with it and forwarded unchanged |
| 11d | **A7 test-data artifact.** The load driver's "dissimilar" names share a first name from a pool of 10. The matcher scores them as close matches (60–69%), so 12,321 synthetic records raised 113,896 duplicate candidates, up to 127 per record, and the bulk `DuplicateCandidates` inserts reached 11 s. The ~120 ms/record A7 floor was measured under this and is inflated: a clean idle batch runs ~20 ms/record | **Done** — the cause was structural, not just similar names: date of birth `n % 80`, first name `n % 10` and sex `n & 1` put every same-day record on one first name and one sex, and behind "Nyandeng" any two 6-letter surnames score ≥ 60%. The driver now builds names from two independent random 8-letter tokens with sex chosen at random (`SyntheticBirths`), held by `SyntheticBirthsTests` against the real matcher. Re-measured on fresh databases: 0 candidates from load (was 166,961), 295 vs 132 records/s at concurrency 16, 26 vs 84 ms per record at concurrency 1. The dev database still holds the earlier runs' data until reset (`scripts/dev-reset.ps1`) |
| 11e | **Side finding, not a test artifact: a common given name can carry two different children over the duplicate threshold.** Real names "Nyandeng Deng" and "Nyandeng Garang", same day, same sex, same facility, score 35 + 10 + 18 = 63 ≥ 60. The whole-string Levenshtein lets a long shared given name dominate. At a busy hospital, with a given name that common, that is a steady stream of false duplicates in the review queue | Needs real national name-frequency data, as the matcher's own notes say. Options: weight tokens by how rare they are, or compare family names separately. Not a guess made on synthetic data |

**District tier findings (11a–11c), reproduced 2026-09-25** against the compose
Postgres, with the API and a District node run from scratch builds and batches
posted through the node:

| Run | What happened |
|---|---|
| 500 records direct to the centre, idle | 200 in 9.9 s |
| 500 records through the District, idle | forwarded in 6.3 s |
| 500 records through the District, 3 s timeout (forced) | Queued. The centre logged the cancellation mid-batch and rolled back: **0 records and no idempotency key**. Retried, timed out again, still Queued |
| 500 records through the District during an A7 burst (16 × 25-record batches) | timed out at 20 s, Queued. Every retry also timed out, one of them because it waited on the device row held by a direct batch from the same device |
| 60 records through the District, 2 s poll | two centre requests 0.8 s apart for one transaction: `200` then `409`. The centre holds all 60 records once. The District says **`Rejected`** |
| Signed 3-record batch direct / through the District, `RequireSignature: true` | direct `200`; through the District `403 SignatureFailed`, **`Rejected`** |

**All three fixed and re-verified live the same day.** One signed 500-record
batch went through the District, to a centre enforcing signatures, with the
poller running every 2 s. It took 51.5 s, well past the old 20 s limit, and
was forwarded on the first attempt. The centre received **one** request,
registered all 500 records, and logged no refusals. The node ran on the store
left by the reproduction runs, which was upgraded in place without losing a
row.

### C. Critical path — needs a device-tooling environment

| # | Task | Notes |
|---|---|---|
| 12 | **B4 field research with midwives and CHWs — start now** | Longest lead time; cannot be parallelised (§15, "the scheduling trap") |
| 13 | MAUI shell: B2 encrypted store, B3 unlock screen, B7 QR printing, B8 scheduled bundle refetch, platform boilerplate | Every piece of offline logic it wraps is built and tested in `client/NCBRS.Client.Core` |

### D. Needs infrastructure

| # | Task | Done when |
|---|---|---|
| 14 | A3 — signing key in an HSM or secret store | Production refuses to start without a real key |
| 15 | A6 — rehearse point-in-time recovery; ship the WAL archive off the host | Restore to a point in time; audit data leaves the box it is written on |
| 16 | A2 deployment step — `REVOKE UPDATE, DELETE ON "AuditLogs"` from the application role | The app role provably lacks the verbs |
| 17 | A7 — national-volume run on production-grade Postgres, fleet across several facilities; re-measure the §A6 RTO at that volume | Sustained burst at projected volume |
| 18 | F1 — reporting replica | Dashboard load leaves registration latency unchanged |
| 19 | G1 — live monitoring: `pg_stat_archiver.failed_count`, outbox backlog, consumer lag | Alerts fire on the failures the runbook describes |

### E. Needs external agreements or providers

| # | Task | Notes |
|---|---|---|
| 20 | E1/E2 — National ID push and amendment propagation | The delivery ledger must be durable and **outside** the consumer's disposable read model, or the documented delete-and-replay recovery re-sends births to an external authority |
| 21 | E3 civil-registry two-way API · E5 statistics-office extract | Data-sharing agreements (§5 item 5) |
| 22 | DHIS2 — differencing across periods and cross-tabulation risk | Review before any scheduled export to an external recipient |
| 23 | H3 SMS confirmation · an email/SMS delivery channel for device alerts | A provider |

### F. Needs people or the organisation

| # | Task |
|---|---|
| 24 | A8 — independent security audit and penetration test |
| 25 | Legal critical path (§5) and the §16 governance actions — none is tracked as complete |
| 26 | G3 tiered support model · G4 training curriculum (after B4) |
| 27 | H1 sync-point workflow — the mechanics exist (district tier, client sync, H2); what remains is operational validation in the pilot |
