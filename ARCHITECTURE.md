# NCBRS architecture

The National Civil Birth Registration System registers births across South
Sudan — from hospitals on the grid to village health posts that may be offline
for weeks. This document ties the whole system together: the tiers, the
services, how a birth flows through them, and the decisions that shape the
design. It is the map; `CLAUDE.md` holds the detailed rationale for each
decision, and the plan documents hold the delivery sequence.

---

## 1. The shape of the problem

Three constraints drive everything:

1. **Offline-first is not a feature, it is the core.** A village post must
   register a birth with zero connectivity and reconcile later. Anything that
   assumes the network — server-generated IDs, live validation, synchronous
   verification — fails the tier the system exists to serve.
2. **These are legal records.** A registration is a person's legal identity.
   Chain of custody, immutability of the audit trail, and never losing or
   silently altering a record outrank convenience everywhere.
3. **The statistics must be usable at national scale.** Coded causes of death,
   WHO/UN vital-statistics structure, and suppression of small cells are not
   polish; they are what makes the output comparable and safe to publish.

## 2. Three tiers

```
  TIER 1 — Facility / village device            TIER 2 — District node        TIER 3 — National centre
  ┌───────────────────────────────┐             ┌──────────────────┐          ┌──────────────────────────────┐
  │ MAUI app  ── wraps ──►         │             │ NCBRS.District   │          │ NCBRS.Api (HTTP only)         │
  │ NCBRS.Client.Core              │  sync batch │ store & forward  │  batch   │  register / amend / annul /   │
  │  · offline registration        │ ──────────► │  (verbatim,      │ ───────► │  certificates / sync / audit  │
  │  · BRN block + PROV- fallback  │  (signed)   │   holds when the │ (signed) │        │                       │
  │  · local outbox                │             │   centre is down)│          │        ▼  outbox (same txn)    │
  │  · offline cert verification   │ ◄────────── │                  │ ◄─────── │  Postgres (system of record) │
  │  · device-key signing          │  outcomes   │  one table:      │ outcomes │        │                       │
  └───────────────────────────────┘             │  batches in      │          │        ▼                      │
        no network at all? signed                │  transit only    │          │  NCBRS.Relay ─► Kafka ─►       │
        USB transfer file ─────────────────────► └──────────────────┘          │  NCBRS.Consumer ─► read model │
                                                                                │   dashboards · DHIS2 export   │
                                                                                └──────────────────────────────┘
                                                                                         ▲
                                                                                 React web app (management)
```

- **Tier 1 — the facility/village client (WS-B).** The device a health worker
  uses at the point of registration. `NCBRS.Client.Core` holds the offline
  logic (below); a .NET MAUI shell adds the screens, the encrypted local store,
  and certificate printing. It generates its own BRNs while offline and syncs
  when it can.
- **Tier 2 — the district node (`NCBRS.District`).** A store-and-forward relay
  on a mini-PC in a district office. It accepts a village post's sync batch and
  forwards it **verbatim** to the centre, holding it when the centre is
  unreachable. It holds no copy of the register — one table, of batches in
  transit — so it is not a second place a birth record can live or drift.
- **Tier 3 — the national centre.** The system of record (Postgres) and the
  HTTP API, plus the event pipeline that feeds reporting. The web management
  app and all reporting live here.

## 3. Services, and why they are split

One repository, several deployables over one shared library. They are separate
because they fail and scale differently.

| Project | Role |
|---|---|
| **`NCBRS.Contracts`** | Models, DTOs, the certificate **verifier**, the admin-geography rules, and the device-signature format. BCL-only, so the Tier-1 device can reference it without Kafka/Npgsql/EF. This is what keeps the canonical forms (certificate payload, signature, provisional identifier) in **one** place rather than reimplemented on the device where they could drift. |
| **`NCBRS.Core`** | `NcbrsDbContext`, the SQLite EF migrations, Kafka options/transport. Referenced by the central services. |
| **`NCBRS.Api`** | HTTP only. Registers, amends, annuls, issues certificates, ingests sync batches. Holds **no** Kafka producer — it stages events in an outbox table and nothing more. |
| **`NCBRS.Relay`** | Drains the outbox to Kafka. Leases rows, so exactly one instance publishes a given message however many API replicas run. |
| **`NCBRS.Consumer`** | Consumes the topics and builds the reporting read model; serves the dashboard and DHIS2 export. The single writer to that store. |
| **`NCBRS.District`** | Tier-2 store-and-forward node (above). |
| **`NCBRS.Migrations.Postgres`** | The Postgres migration set (histories are per-provider and cannot be shared). |
| **`client/NCBRS.Client.Core`** | The Tier-1 offline logic (§7). UI-free, references only Contracts, unit-tested on any host. |

The split's guarantee: **a broker outage stalls delivery without touching
registrations, and no worker can take the registration API down with it.**

## 4. How a birth flows

**Online (a hospital terminal).** `POST /api/birthrecords/register` →
`BirthRegistrationService` allocates/validates the device-supplied BRN, writes
the `BirthRecord`, stages a `BirthRegisteredEvent` in the outbox **before**
`SaveChanges` (so the event commits with the domain write), and writes the
audit rows. The Relay publishes; the Consumer projects.

**Offline (a village post).** The device registers into its **local outbox**
with a BRN drawn from its granted block. When connectivity returns it uploads a
**signed batch**; the centre ingests each record, answers per-record
(registered / duplicate / rejected), and the device settles its outbox —
keeping exactly the rejected records queued. With no network at all, the same
signed batch travels as a **transfer file** on removable media to a sync point.

**Through the district (Tier 2).** The village post's batch reaches the centre
via the district node, which forwards it verbatim. *Queued* is `202`,
*forwarded and answered by the centre* is `200`, so a family is never told a
registration is confirmed when the node has only buffered it. Idempotency is
end-to-end by the caller's transaction id: the same transaction arriving twice
(once forwarded, once direct) produces one batch and one set of records.

## 5. The event backbone and reporting

The **database is always the system of record**; events are an outbound
notification of what already happened (draft 6.4.1). The outbox + Relay make
that structural: the request path never touches Kafka, so a broker outage
cannot delay or affect a registration at all.

Topics: `ncbrs.birth-records.registered`, `.amended`, `.annulled`,
`ncbrs.outcomes.neonatal`, `.maternal`, `ncbrs.sync.audit`. Delivery is
**at-least-once** (a relay that crashed after publishing; a deliberate replay),
so the projection is **idempotent by construction**: facts are keyed by BRN and
never counters, so writing one twice still says what it said. Corrections for a
BRN not yet seen are **held** and applied in occurrence order, so a replay from
offset 0 cannot count an annulled birth as live.

The Consumer serves the **dashboards** and the **DHIS2 aggregate export**. The
rule that outranks the arithmetic: an indicator whose inputs are unknown
reports **null and says why — never zero** (a Ministry reading "0 neonatal
deaths" concludes the month went well; "not available" sends someone to look).
The DHIS2 export shares only district-months, never a person, and suppresses
small cells — whole breakdowns at a time, because suppressing one cell of a
published total is arithmetic, not suppression.

## 6. Identity, trust, and the boundaries

- **Callers** authenticate to Keycloak (realm roles: facility-registrar,
  district-officer, ministry-admin). The API is a pure resource server that
  stores no credentials.
- **Scope is the county.** A caller sees their own county, resolved by walking
  their facility up the administrative tree (`CountyLookup`); scoping
  **refuses** rather than silently narrowing. Ministry admins are national.
- **Devices** have a second, separate proof. *Enrolment* says a device id is
  one the Ministry issued to a facility. *Possession* says the request actually
  came from that device, via a signature over the **raw request body**
  (`DeviceSignature`, shared by device and server) — so a stolen token is not
  enough. Signing the raw bytes is what lets the district node forward a batch
  verbatim and lets a transfer file survive on a USB stick.
- **The audit trail is append-only, enforced at the database** (triggers, not
  convention), because a corrected audit row is indistinguishable from a
  falsified one.
- **A certificate is valid only if signed AND not revoked.** A signature cannot
  know the register was corrected after issuance, so revocation is a second,
  signed check. Verification works **offline** against a cached, signed
  revocation list; a stale cache answers *Unknown*, never *valid*. Signing keys
  rotate: retired keys stay trusted (they signed genuine documents), and the
  key id in the QR selects the key rather than trying each.

## 7. The Tier-1 client-core (WS-B)

`client/NCBRS.Client.Core` is the offline brain the MAUI shell wraps. It is
UI-free and references only Contracts, so its correctness-critical logic is
unit-tested (and demonstrated by `client/NCBRS.Client.Harness`) with no device
tooling. The shell owns only the screens, the encrypted store, and printing.

| Component | Does |
|---|---|
| `DeviceSigner` | Device key generation + raw-body signing (B9) |
| `OfflinePinLock` | Offline PIN unlock, locally rate-limited (B3) |
| `DeviceBrnAllocator` | BRN block consumption, low-block warning, `PROV-` fallback (B5) |
| `SyncOutbox` | Local outbox, batch building, settlement (B6) |
| `CachedVerificationBundle` | Offline certificate verification + refresh signal (B8) |
| `OfflineTransferFile` | Signed offline batch transfer (H2) |
| `FacilityClient` | Composes the above into the registration workflow |

The **BRN block design** is the linchpin of offline-first. The server
pre-allocates a block to a device; the device generates BRNs from it locally,
so weeks offline never collide with a block granted elsewhere. Exhaust the
block and it issues a loud `PROV-` provisional identifier — deliberately *not*
a BRN — which the centre replaces with a real number at reconciliation. The
centre confirms every submitted BRN against the block it actually granted, so
device-generated numbers are trusted only once checked. See
`client/INTEGRATION.md` for how the shell drives all of this.

## 8. Data model

Follows WHO/UN vital-statistics standards, not an ad hoc field list:

- `BirthRecord.VitalEventType` separates a live birth from a fetal death; a
  death following a live birth is **always a second record** (`NeonatalOutcome`
  via ICD-PM, `MaternalOutcome` via ICD-MM), never a field on the birth.
- Causes of death are **coded**, never free text.
- Statistical variables live in `MaternalStatistics`, separate from the legal
  `BirthRecord`, coded to ISCED/ISCO — and never gating a registration or a
  certificate.
- **Administrative geography** is one self-referencing `AdministrativeArea`
  tree (Country → State → County → Payam/Block → Boma/Quarter → Village),
  seeded from the official COD-AB. A facility's `CountyCode` is a denormalised
  scope key beside its precise `AdministrativeAreaId`; scoping, audit and
  reporting all resolve to the county.

Three acts stay distinct and must never merge: **amendment** (the record
described a real birth wrongly), **duplicate supersession** (two records, one
child), **annulment** (there was no such birth — its own topic, ministry-level,
nothing deleted, BRN never reissued).

## 9. Persistence and deployment

- **Postgres** is the central system of record (draft 6.4/7.1). **SQLite** is
  the dev/test provider and is used deliberately by two tiers: the district
  node (one table of batches in transit) and the consumer's read model
  (rebuildable from Kafka by design). `Database:Provider` selects, resolved in
  one place; an unrecognised value is refused at startup, never defaulted.
- **Migrations are per-provider** — a migration added to the SQLite set (Core)
  must be added to the Postgres set (`NCBRS.Migrations.Postgres`). The API
  applies migrations on startup in Development only; other tiers apply them as a
  deployment step (several services racing to migrate one database corrupts it).
- **`docker-compose.yml`** runs Postgres (published on 5433), Kafka, and
  Keycloak with an imported realm. Continuous WAL archiving is configured to a
  separate volume; a restore drill has been run, and the append-only audit
  triggers come back enforcing with the data.

## 10. The web management app and the generated contract

`web/` is a React + TypeScript + Vite app (shadcn/ui) for the central Ministry
and district staff: register/search, the review queues (amendments, duplicates,
late registrations, annulments), certificates, devices and device-silence
alerts, the dashboard, the DHIS2 export, and recording outcomes.

Both HTTP services **generate an OpenAPI document at build time**, committed to
`web/openapi/`, and the web client's types are generated from it; CI fails if
either is stale. This is why React was chosen over Blazor — a document that
misdescribes the service costs that decision its value — and why the number- and
enum-typing rules in the generator are pinned by tests.

## 11. Environments

- Local dev DB: SQLite (`ncbrs.db` at the repo root) by default;
  `docker compose up postgres` + `Database__Provider=Postgres` runs the real
  engine. The API applies migrations and seeds the SS geography, a
  representative dev facility fleet, and demo births on Development startup.
- Certificate signing refuses to start outside Development without a configured
  PFX; the dev fallback mints a throwaway key.

## 12. Further reading

- **`CLAUDE.md`** — the detailed rationale for every decision above, section by
  section. The source of truth for *why*.
- **`NCBRS-Business-and-Delivery-Plan.md`** — the workstreams (WS-A…WS-H), the
  gap analysis (§12) and the sequencing (§15).
- **`NCBRS-Web-Plan.md`** — the web front end's plan and the backend gaps it
  forced.
- **`client/README.md` and `client/INTEGRATION.md`** — the Tier-1 client and
  how a MAUI shell wires onto it.
- **`CONTRIBUTING.md`** — the delivery workflow and approval gates.
