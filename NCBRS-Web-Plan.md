# NCBRS Central Management Site — plan and to-do list

The web application district officers and ministry staff use to run the
register. Companion to `NCBRS-Business-and-Delivery-Plan.md`, which remains
the plan of record for the platform as a whole; this covers the front end and
the backend work it forces.

---

## 1. What this is, and what it is not

**It is** the central tier's human interface: searching and reading the
register, working the review queues that the legal workflows create, managing
devices and facilities, and reading the §10 indicators.

**It is not** the facility client. WS-B1–B8 is a tablet application for
midwives and community health workers, offline-first, with local storage and
certificate printing. It is a separate codebase with a separate audience, and
the plan calls it the programme's critical path. Nothing here replaces it.

The distinction matters for scope: **this site may assume connectivity**. It
is used from district offices and the Ministry, not from a village post. That
assumption is what makes a browser application appropriate at all, and it
should be stated in any decision record — the moment someone proposes using
this site at a health post, the answer is that it was never built for it.

### Who uses it

| Role | What they do here |
|---|---|
| `district-officer` | Approve amendments, verify late registrations, rule on duplicates, resolve conflicts, enrol and manage devices, watch silence alerts |
| `ministry-admin` | All of the above, plus annulment, facilities and BRN blocks, exports, national dashboard |
| `facility-registrar` | Look up a record, register online, request a correction, issue and reprint certificates |
| `community-health-worker` | Little or nothing — their workflow is the tablet |

The role determines the navigation, not just the permissions. A facility
registrar shown four empty review queues learns to ignore the navigation.

---

## 2. Decision required before step 1: the stack

This shapes every task below and should be recorded like B1 was.

**Recommended: React + TypeScript + Vite, with the API client generated from
the existing OpenAPI document.**

| | React + TypeScript | Blazor WebAssembly |
|---|---|---|
| Contract drift | Generated from OpenAPI, so it cannot drift silently | Shares `NCBRS.Core` DTOs directly — strongest possible |
| Team | One more language and toolchain to maintain | One language across the whole platform |
| Hiring | Large pool | Small pool, especially locally |
| Data grids, queue UIs, forms | Mature ecosystem | Workable, thinner |
| Initial download | Small | Several MB before first paint |

The generated client is what makes the recommendation defensible: Blazor's
real advantage is that a DTO change breaks the build rather than production,
and generating TypeScript from Swagger buys most of that back. If the
Ministry's standing team is .NET-only and will stay that way, Blazor WASM is
the better answer and the to-do list below barely changes — only the tasks
marked **[stack]**.

**Blazor Server is not recommended**: it needs a live WebSocket per user, and
a dropped connection loses UI state mid-form. For a form that creates a legal
record, that is the wrong failure mode.

---

## 3. Architecture

```
Browser ── OIDC (auth code + PKCE) ──▶ Keycloak :8080
   │
   ├── /api/*        ──▶ NCBRS.Api      :5259   register, amend, certificates, devices
   └── /api/dashboard,
       /api/exports  ──▶ NCBRS.Consumer :5281   indicators, DHIS2 export
```

Two origins, on purpose: the consumer owns the reporting store and putting
dashboard load on the registration API is what plan F1 exists to prevent.

**Two ways to handle that, and it is a real decision:**

- **CORS on both services.** Simplest. The browser holds the access token.
- **A reverse proxy in front of both**, so the browser sees one origin. Adds a
  component, but removes CORS entirely and allows a future BFF that keeps
  tokens out of the browser.

Recommended: **CORS now, proxy when the site is deployed** — the security
benefit of a BFF is real but belongs with the deployment topology, not with
the first screen.

### Token handling

A public SPA client with PKCE, access token in memory, refresh handled by the
Keycloak JS adapter or `oidc-client-ts`. **Not `localStorage`** — an access
token that survives a tab close is an access token an XSS payload can
exfiltrate at leisure, and this one can withdraw a legal identity.

`ncbrs-device` must not be reused: it has direct access grants enabled, which
means a password grant. A browser application that collects a password is
one that can be phished into collecting it for someone else, and it
contradicts the platform's rule that the API never handles credentials.

---

## 4. Backend work this forces

The site cannot be built from the current API alone. These are backend tasks,
and they are the ones most likely to be underestimated:

| # | Gap | Why the site needs it |
|---|---|---|
| **W1** | **Record search** — by name, date range, facility, district, status | `GET /api/birthrecords/{brn}` is exact-BRN lookup only. A registrar helping a family who lost their certificate has a name and an approximate date, not a number. **This is the single largest backend gap.** |
| **W2** | **Facilities list and detail**, incl. BRN block state | No endpoint exists at all; there is no way to see which facility is near block exhaustion. |
| **W3** | **Registrar / user directory** | Queues show `registrarId` GUIDs today. A queue that names "0199a1b2-…" instead of a person is a queue nobody can audit. |
| **W4** | **Audit log query** — by record, actor, device, date | The audit trail is legally load-bearing and currently readable only by SQL. |
| **W5** | **CORS** on both services | Nothing in the browser works without it. |
| **W6** | **SPA Keycloak client** with redirect URIs and PKCE | As above. |
| **W7** | **Pagination contract** across all list endpoints | Every queue endpoint returns an unbounded list today. At national volume that is a denial of service against the Ministry's own dashboard. |

W1 carries a privacy consequence worth deciding deliberately: **a name search
across the national register is a surveillance capability.** It needs to be
role-gated, logged to `AuditLog` like any other access, and probably scoped to
the caller's district unless they hold a ministry role. Decide that before
building it, not after.

---

## 5. To-do list

Ordered so each phase is usable on its own. **[stack]** marks tasks whose
content depends on the §2 decision.

### Phase 0 — Make a browser able to talk to the platform
- [ ] **W5** Add CORS to `NCBRS.Api` and `NCBRS.Consumer`; allowed origins from configuration, credentials off, no wildcard
- [ ] **W6** Add an `ncbrs-web` public client to the Keycloak realm: PKCE required, direct access grants **off**, redirect and post-logout URIs for dev and staging
- [ ] **W7** Agree and implement a pagination envelope for list endpoints; apply to the four existing queues
- [ ] Confirm the OpenAPI document covers every endpoint and enum accurately

### Phase 1 — Skeleton that proves the risky parts
- [ ] **[stack]** Scaffold the project, routing, build and lint
- [ ] **[stack]** Generate the typed API client from OpenAPI; wire it into CI so a drifting contract fails the build
- [ ] Wrap the request/response envelope (`{envelope, data}` out, `{meta, data}` back) once, centrally, including the transaction id header
- [ ] OIDC login, silent refresh, logout; token in memory only
- [ ] Role-aware shell: navigation reflects what the signed-in role can actually do
- [ ] Error surface that renders the API's `errors[]` against the right form fields — the API already returns field paths, so the UI should never show a bare "something went wrong"
- [ ] **Vertical slice: look up a BRN and display the record.** Ends the phase by proving auth, envelope, error handling and rendering end to end

### Phase 2 — The register
- [ ] **W1** Record search backend, with the district-scoping decision recorded
- [ ] Search UI and results
- [ ] Record detail: identity, status, certificate state, provisional identifier, annulment block
- [ ] Amendment history timeline, showing previous values
- [ ] Online registration form, incl. late-registration evidence when the window has passed
- [ ] Request a correction, with the two-track outcome made visible: applied now vs queued for approval (the 202 case must not look like a failure)

### Phase 3 — The review queues
*The legal heart of the site. Each queue is a decision with consequences, and
each needs the reason for the decision captured.*
- [ ] Amendment approvals — including the refusal when the record moved since submission
- [ ] Amendment conflicts — per-field, showing what the device saw against what the register held
- [ ] Late registration verification — evidence, declarant, and the rule that the verifier may not be the filer
- [ ] Duplicate review
- [ ] Annulment (ministry only), with its three-way distinction from amendment and duplicate stated in the UI itself
- [ ] **W3** Registrar directory so every queue shows a person, not a GUID

### Phase 4 — Certificates
- [ ] Issue, with the reasons issuance is refused shown plainly (fetal death, unverified late registration, unreconciled provisional, annulled)
- [ ] Reprint, showing reprint count and that the signature and issue date do not change
- [ ] Revocation list view
- [ ] Verify a certificate by pasting a scanned QR payload

### Phase 5 — Devices and facilities
- [ ] Device list with `lastSeenAtUtc`, including the never-reported state
- [ ] Enrolment — public key only, with a visible refusal if a private key is pasted
- [ ] Suspend, reinstate, revoke, each requiring a reason
- [ ] Silence alert queue; acknowledge with a note, and make clear acknowledging is not resolving
- [ ] **W2** Facilities list, BRN block state, low-block warning
- [ ] Grant a BRN block

### Phase 6 — Dashboard and exports
- [ ] National summary with district drill-down
- [ ] **Render "not available" distinctly from zero.** The API is careful to return null rather than 0; a UI that prints `0` throws that away and tells a Ministry the month went well
- [ ] Show the `stillFilling` flag on recent periods
- [ ] Show `notAvailable[]` so the missing §10 indicators are visible rather than absent
- [ ] Time-to-registration by facility tier
- [ ] DHIS2 export screen, listing suppressions alongside the values
- [ ] **W4** Audit log viewer

### Phase 7 — Fit for a Ministry
- [ ] Accessibility pass to WCAG 2.2 AA — this is a government service
- [ ] Responsive down to a district officer's laptop; tablet-friendly for supervision visits
- [ ] Session expiry that warns before it drops a half-completed registration form
- [ ] Empty, loading and failure states for every queue
- [ ] End-to-end tests over the critical paths: register, amend and approve, issue, annul
- [ ] Localisation scaffolding, even if only one language ships

---

## 6. Sequencing and risk

The dependency order is tight at the start and loose afterwards: **Phase 0
blocks everything**, Phase 1 blocks everything after it, and Phases 2–6 are
largely independent once the shell exists.

Three risks worth naming now:

- **W1 (search) is the most underestimated item here.** It is a new query
  surface over the register's most sensitive data, with indexing, pagination
  and a privacy decision attached. It is not "add a WHERE clause".
- **The queues are where the legal design lives.** A UI that lets a district
  officer approve their own submission, or that shows an annulment as a kind
  of amendment, undoes work the backend does carefully.
- **This site is a second consumer of the API contract.** Until now the API
  had no real client, so contract mistakes were invisible. Generating the
  client from OpenAPI is what keeps that honest.
