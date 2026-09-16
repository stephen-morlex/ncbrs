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

## 2. The stack — decided

**React + TypeScript + Vite, styled with Tailwind CSS and built on
shadcn/ui, with the API client generated from the existing OpenAPI
document.**

| Layer | Choice |
|---|---|
| Build | Vite |
| Language | TypeScript |
| UI components | shadcn/ui (Radix primitives, Tailwind CSS v4) |
| API client | Generated from OpenAPI, checked in CI |
| Auth | Keycloak, OIDC authorization code + PKCE (§4) |

The generated client is not optional decoration. It is the term that made
React defensible against Blazor, whose real advantage was that a DTO change
breaks the build rather than production — so it belongs in CI from the first
commit, failing the build when the contract moves.

| | React + TypeScript | Blazor WebAssembly |
|---|---|---|
| Contract drift | Generated from OpenAPI, so it cannot drift silently | Shares `NCBRS.Core` DTOs directly — strongest possible |
| Team | One more language and toolchain to maintain | One language across the whole platform |
| Hiring | Large pool | Small pool, especially locally |
| Data grids, queue UIs, forms | Mature ecosystem | Workable, thinner |
| Initial download | Small | Several MB before first paint |

**Blazor Server is not recommended**: it needs a live WebSocket per user, and
a dropped connection loses UI state mid-form. For a form that creates a legal
record, that is the wrong failure mode.

### What choosing shadcn/ui actually commits us to

shadcn/ui is **not an npm component library**. Its own documentation is
explicit: *"This is not a component library. It is how you build your
component library."* The CLI copies component **source** into
`src/components/ui/`, and from that moment the project owns it.

That property cuts both ways, and both directions matter here:

- **In our favour.** A birth certificate form and a district officer's review
  queue are not generic UI. Owning the source means adapting a component to
  the workflow rather than fighting a library's abstraction with wrapper
  components and style overrides. There is also no runtime dependency to go
  unmaintained under a system with a ten-year horizon.
- **Against us.** There is **no upgrade path**. A fix published upstream does
  not arrive by bumping a version; someone has to notice it and port it. For
  a Ministry system maintained by a small team, that is a standing obligation,
  and it should be written into the maintenance plan (WS-G) rather than
  discovered when an accessibility bug is found in a dialog three years from
  now.

**The accessibility argument is the strongest one.** shadcn/ui builds on
Radix primitives, which handle focus management, keyboard navigation and ARIA
semantics properly. Phase 7 commits this site to **WCAG 2.2 AA** because it is
a government service, and starting from Radix means that target is a review
rather than a rewrite. Hand-rolled dialogs and comboboxes are where that
target normally dies.

**Tailwind v4 comes with it**, via the `@tailwindcss/vite` plugin rather than
a PostCSS config. That is a styling decision made by accepting shadcn, not a
separate one, and it should be recorded as such.

Two conventions to set on day one, while there is nothing to migrate:

- **Components under `src/components/ui/` are treated as vendored source.**
  Edit them deliberately, and note what was changed, so a future port of an
  upstream fix can tell our changes from theirs.
- **Domain components never live there.** `BirthRecordCard` is ours;
  `button.tsx` is vendored. Mixing them makes the first rule unenforceable.

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

---

## 4. Authentication and authorisation — Keycloak

Keycloak is already the platform's identity provider (a settled deviation
from the draft: the system must manage users *and* client applications, and
the API stays a pure resource server that never stores credentials). The site
does not change that; it becomes a second client of the same realm.

### The flow

**OIDC authorization code with PKCE**, against a **new public client
`ncbrs-web`**. No client secret — a secret shipped to a browser is not a
secret.

**`ncbrs-device` must not be reused.** It has direct access grants enabled,
which is the password grant: the application would collect the user's
password itself. An application that collects passwords is one that can be
phished into collecting them for someone else, and it contradicts the rule
that the API never handles credentials. The new client has direct access
grants **off**.

Realm configuration for `ncbrs-web`:

| Setting | Value |
|---|---|
| Client type | Public |
| Standard flow | On |
| Direct access grants | **Off** |
| PKCE method | `S256`, required |
| Valid redirect URIs | Exact dev and staging URIs — never `*` |
| Valid post-logout redirect URIs | Exact |
| Web origins | Exact origins, not `+` or `*` |

A wildcard redirect URI is an open redirect, and an open redirect on an OIDC
client is a way to have an authorization code delivered somewhere else.

### Library

**`oidc-client-ts` with `react-oidc-context`**, rather than `keycloak-js`.

The reason is not preference. `keycloak-js` renews sessions through a hidden
iframe against the Keycloak session cookie, and browsers now partition or
block third-party cookies by default — which makes silent renewal fail
intermittently, in a way that looks like random logouts to a district officer
halfway through a registration form. `oidc-client-ts` renews with a refresh
token and does not depend on third-party cookie behaviour.

It is also standards-based OIDC. The only Keycloak-specific thing in the
token is where roles sit, which is a few lines of parsing rather than a
reason to take a coupled adapter.

### Token storage

**Access token in memory only. Never `localStorage`.** A token that survives
a tab close is a token an XSS payload can exfiltrate at leisure — and this
one can annul a birth registration. Refresh handled by the library, with the
session ending when the tab does.

This is the position to revisit if a BFF is introduced (§3): the strongest
answer is that the browser holds no token at all and the proxy keeps it in a
server-side session. That belongs with the deployment topology, and the
memory-only rule is what holds until then.

### Authorisation

Roles arrive in the token under `realm_access.roles`: `facility-registrar`,
`community-health-worker`, `district-officer`, `ministry-admin`.

**The front end uses roles for navigation only. Authorisation is enforced
server-side, always.** Hiding a button is a courtesy to the user, not a
control — the API's policies (`CanApproveAmendments`,
`CanAnnulRegistrations`, `CanEnrolDevices`, and the rest) are the control,
and they already exist. Anyone reasoning about who can annul a registration
should be reading `Program.cs`, not a React component.

Two rules that follow:

- **Mirror the API's policy names in one module**, so navigation and the
  server agree by construction rather than by someone remembering. A screen
  the UI offers and the API refuses is a bug report from a user; a screen the
  UI hides and the API allows is a control nobody is enforcing.
- **The site must handle a 403 gracefully anyway.** Roles change while a
  session is open, and a token minted before a role was withdrawn stays valid
  until it expires. The UI should say what happened rather than showing an
  empty page.

Beyond roles, the API enforces rules the front end cannot and must not try
to: a reviewer may not approve their own submission, and a late registration
may not be verified by the registrar who filed it. The UI should explain
those refusals, never pre-empt them.

---

## 5. Backend work this forces

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

### W1's privacy decision — decided

**A name search across the national register is a surveillance capability**,
so W1 is **scoped to the caller's own district, with `ministry-admin`
exempt**, and **every search is written to `AuditLog`** like any other access
to the register.

Two consequences to build in rather than bolt on:

- The scope is enforced **server-side, from the token**, never from a
  parameter the client sends. A district id in a query string is a district
  id a caller can change.
- A registrar helping a family who moved districts will hit this, and that is
  the cost of the decision, not a bug in it. The answer is a referral to the
  Ministry, not a quiet widening of the scope later.

Searching by exact BRN stays unrestricted — a family holding a certificate
with that number on it is not a search, it is a lookup.

---

## 6. To-do list

Every phase below is delivered through the workflow in `CONTRIBUTING.md`:
one branch per coherent unit of work, tests run before the commit, and
approval gates at commit, PR and merge.

Ordered so each phase is usable on its own. **[stack]** marks tasks whose
content depends on the stack chosen in §2.

### Phase 0 — Make a browser able to talk to the platform
*No UI yet. Everything here is backend and realm configuration, and none of
it is visible — which is exactly why it gets skipped and then blocks Phase 1.*

- [ ] **W5** Add CORS to `NCBRS.Api` and `NCBRS.Consumer`; allowed origins from configuration, no wildcard, credentials off (the token travels in the `Authorization` header, not a cookie)
- [ ] **W6** Add the `ncbrs-web` public client to `keycloak/ncbrs-realm.json` per §4: standard flow on, direct access grants **off**, PKCE `S256` required, exact redirect/post-logout URIs and web origins
- [ ] Verify the realm import still works from a clean `docker compose down -v && up`
- [ ] **W7** Agree and implement a pagination envelope for list endpoints; apply to the four existing queues
- [ ] Confirm the OpenAPI document covers every endpoint, enum and the `{meta, data}` envelope accurately — it is about to become a build input, not just documentation

### Phase 1 — Skeleton that proves the risky parts
*Ends with one screen. The point is not the screen; it is that auth, the
envelope, error handling and the generated client are proven together before
anything is built on them.*

**Scaffold**
- [ ] `npm create vite@latest web -- --template react-ts` under a new top-level `web/` directory
- [ ] `npm install tailwindcss @tailwindcss/vite` and replace `src/index.css` with `@import "tailwindcss";`
- [ ] `npm install -D @types/node`; add `baseUrl` and the `@/*` → `./src/*` path alias to **both** `tsconfig.json` and `tsconfig.app.json`
- [ ] Add the matching `resolve.alias` for `@` and the `tailwindcss()` plugin to `vite.config.ts` — the alias must be set in both places or the editor and the build disagree
- [ ] `npx shadcn@latest init`, then add components as needed (`npx shadcn@latest add button table dialog form …`)
- [ ] Commit `components.json` and treat `src/components/ui/` as vendored source per §2
- [ ] ESLint, Prettier, and a CI job running lint, typecheck, build and tests

**Contract**
- [ ] Generate the typed API client from the OpenAPI document into `src/api/generated/`
- [ ] Wire generation into CI so a drifting contract fails the build rather than production
- [ ] Wrap the request/response envelope once, centrally (`{envelope, data}` out, `{meta, data}` back), including the transaction id header

**Auth (§4)**
- [ ] `oidc-client-ts` + `react-oidc-context` against `ncbrs-web`; authorization code + PKCE
- [ ] Access token in memory only; refresh via refresh token, never an iframe
- [ ] Login, logout, post-logout redirect, and the callback route
- [ ] Parse `realm_access.roles`; expose them through one module that mirrors the API's policy names
- [ ] Route guards for navigation, plus a graceful 403 screen — the server is the control, the UI is a courtesy

**Shell**
- [ ] App layout: navigation, header with signed-in user and role, sign-out
- [ ] Role-aware navigation, so a facility registrar is not shown four empty review queues
- [ ] Error surface that renders the API's `errors[]` against the right form fields — the API returns field paths, so the UI should never show a bare "something went wrong"
- [ ] Loading, empty and failure states as shared components, decided once
- [ ] **Vertical slice: look up a BRN and display the record**

### Phase 2 — The register
- [ ] **W1** Record search backend: district-scoped from the token, ministry exempt, every search audited
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

## 7. Sequencing and risk

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
- **shadcn/ui has no upgrade path** (§2). Owning the component source is the
  right trade for a workflow-specific UI, but porting upstream fixes becomes
  a standing maintenance obligation and belongs in WS-G's plan, not in
  somebody's memory.
