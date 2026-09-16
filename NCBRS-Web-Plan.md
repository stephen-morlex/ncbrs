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

Three conventions to set on day one, while there is nothing to migrate:

- **Search the registry before writing a component.** shadcn is the default
  and a hand-written component is the exception that has to be argued for.
  The registry is much wider than the well-known handful — `empty`,
  `spinner`, `alert`, `pagination`, `sidebar` and `breadcrumb` are all in it,
  and each is one somebody would otherwise write badly from scratch. Check
  with `npx shadcn@latest search @shadcn -q <term>` and read it with
  `npx shadcn@latest view @shadcn/<name>`.

  This is not a style preference. Every hand-rolled control is one more place
  to get focus traps, `aria-*` wiring and keyboard order wrong, and §7's
  WCAG 2.2 AA obligation is assessed against the whole site, not against the
  parts that happened to use the library. A custom component is justified by
  domain meaning — `BirthRecordCard`, `ConflictDiff` — never by a control the
  registry already ships.
- **Components under `src/components/ui/` are treated as vendored source.**
  Edit them deliberately, and note what was changed, so a future port of an
  upstream fix can tell our changes from theirs.
- **Domain components never live there.** `BirthRecordCard` is ours;
  `button.tsx` is vendored. Mixing them makes the first rule unenforceable.

The shadcn MCP server exposes this same registry to an assistant directly,
and `.mcp.json` at the repo root configures it for everyone. It is
**pinned** — `shadcn@4.21.0`, matching `web/package-lock.json`, not
`@latest` as `shadcn mcp init` writes it. `@latest` resolves and executes
whatever npm serves at the moment it runs, on every machine that opens this
repo; that is the same reasoning as `npm ci` in CI, applied to tooling.
Bump it with the lockfile, not separately.

The server is a convenience either way: `npx shadcn@latest search` reaches
the identical registry, and MCP config is only read when a session starts.

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
| **W2** | **Facilities list and detail**, incl. BRN block state — **done, PR #19** | Premise verified before building: two endpoints took a `facilityId`, but nothing could produce one or say anything about a facility. |
| **W3** | **Registrar / user directory** — **done, PR #15** | The gap was narrower than this line claimed; see below. |
| **W4** | **Audit log query** — **done, PR #16** | The trail is legally load-bearing and was readable only by SQL. W1 made that worse: it writes search criteria there, and nobody without database access could see them. |
| **W5** | **CORS** on both services | Nothing in the browser works without it. |
| **W6** | **SPA Keycloak client** with redirect URIs and PKCE | As above. |
| **W7** | **Pagination contract** across all list endpoints — **done** | Every queue endpoint returned an unbounded list. At national volume that is a denial of service against the Ministry's own dashboard. |
| **W8** | **OpenAPI for `NCBRS.Consumer`** | Found by the Phase 0 audit: the dashboard and export endpoints have no document, so the generated client cannot cover them — and the generated client is why React was chosen over Blazor. |

### W4 as built

`GET /api/audit` — `GetAuditTrail` — filtered by record, actor, device, entity
type and date, paged newest first. PR #16.

A control nobody can consult only works in retrospect, after a dispute has
already escalated to whoever holds the database credentials. W1 sharpened
that: it writes the names people searched for into the trail, and until now
those rows were invisible to everyone the control was meant to serve.

**Reading the trail is written to the trail**, with the filter recorded.
"Read the trail" and "read every entry for this one registrar" are different
acts, and only the second looks like checking up on a colleague. A refused
read writes nothing — nothing was disclosed, so there is nothing to account
for.

This corrects a claim made in W3. That PR justified adding no audit row on
the grounds that `RequestLog` already records every `/api` call. It does —
but it stores method, path, status and duration, and **not who made the
call**. For a directory read that is an acceptable gap; for the one endpoint
that can read every other endpoint's record of itself, it is not.

Two scopes, because there are two questions:

- **"What happened to this record?"** — given a `brn`, every entry against it
  whoever acted. Gated on the record being in the caller's district, and a
  record outside it answers **404**, identically to one that does not exist:
  a 403 would confirm the BRN exists elsewhere, which is what the scope
  withholds.
- **"What have my people done?"** — without a `brn`, entries whose actor is a
  registrar in the caller's district.

**A real limitation, named rather than hidden:** an action taken on a
district's records by someone outside it does not appear in that district's
unfiltered view. Closing it properly needs a district on the row itself, and
that cannot be backfilled — `AuditLogs` is append-only and enforced so at the
database, so existing rows can never be given one. Any fix applies to future
rows only, and the gap in the history is permanent.

Entries name the actor rather than their id, resolved in one query rather
than per row. `ActorName` is null where no person acted — a sweep, a
projection — which is different from a blank name.

`CanReadAuditTrail` is its own policy rather than borrowed from another
oversight role, and is mirrored in the web client's policy table.

#### Bundle splitting — PR #18

One 525 kB chunk became a vendor chunk, an application chunk, the shell and a
chunk per page.

| | before | after |
|---|---|---|
| First paint | 525 kB / 162 kB gz | 378 kB / 118 kB gz |
| **Repeat visit after a deploy** | 525 kB / 162 kB gz | **49 kB / 19 kB gz** |
| Vendor (cached across deploys) | — | 329 kB / 100 kB gz |
| Shell | — | 116 kB / 37 kB gz |
| Each page | — | 5–11 kB |

**The recurring cost is the one that matters here.** Total bytes on a first
cold visit barely moved — the shell is needed the moment sign-in returns. What
changed is that a district office opening this daily re-downloads 19 kB
gzipped rather than 162 kB, because application code changes on every deploy
and React, the router and the OIDC client do not.

`AuthCallback` and `RequireAuth` are deliberately **not** lazy. They run
before any route renders, so deferring them would put a chunk fetch in front
of the app even deciding whether the user is signed in — a round trip added
to the one path every visit takes.

One `Suspense` boundary around the layout rather than one per page: on a cold
visit both chunks are in flight together, and a nested boundary would flash a
second spinner inside a shell that had only just appeared.

Verified against the **production build**, not the dev server, which serves
modules individually and would have proved nothing. Opening `/records`
fetched the entry, vendor, shell and that page; `AuditTrail` and
`RecordSearch` were absent. Navigating to `/records/search` then fetched
`RecordSearch` and `table` on demand, and `AuditTrail` still never arrived.

Note `sonner` and `next-themes` are in `package.json` but imported nowhere, so
they are already tree-shaken out of the bundle. They are manifest weight
rather than payload, and should go when something confirms nothing needs them.

#### The screen — PR #17

`/audit`, under **Oversight** rather than beside the register: reading the
trail is checking on the work, not doing it. A record's own history is one
click from the record, which is where a dispute starts.

The screen says plainly that **opening the trail is recorded in it**. Not as
a deterrent — a district officer checking a disputed record is doing their
job. It is there because that read and someone checking what a colleague
searched for look identical, and the person doing the first should know the
second is not invisible either.

Two things the table is careful about:

- **"no person" rather than a blank actor.** A sweep raising a device alert
  has no actor, and an empty cell reads as missing data rather than as an act
  nobody performed.
- **"district not recorded" rather than an empty cell**, for rows written
  before the column existed. A blank reads as a bug; the label says what it
  actually is, and that it can never be filled in.

Timestamps render in UTC as stored, with the time. Unlike a date of birth, an
audit entry *is* an instant — and "who acted first" is a question this table
exists to answer, so it must read the same in two offices in different
offsets.

Verified against the running stack: a search wrote
`Search:name=Naledi;returned=4;total=4` against `D-CENTRAL-07`, opening the
trail wrote `Read:all;returned=50;total=116` against the same district, and
rows written before the migration show "district not recorded" beside rows
that carry one. The two eras are visually distinct on the screen.

### W3 as built — and how the gap was overstated

`GET /api/registrars` and `GET /api/registrars/{registrarId}` — PR #15.

**The plan said "Queues show `registrarId` GUIDs today". That was no longer
true when the work started.** `PendingAmendmentResponse`,
`AmendmentConflictResponse`, `PendingLateRegistrationResponse` and
`AmendmentHistoryEntry` all carry `…RegistrarName` beside the id; the
duplicates queue has no registrar at all, because a duplicate is detected by
the system rather than filed by a person. The names were added as those
queues were built, and this line was not revisited.

What was genuinely missing is the directory itself: a way to resolve an id
the UI meets anywhere, and an answer to "who is provisioned here" — which a
district officer needs before they can notice an account that should have
been withdrawn.

Two boundaries, both tested:

- **District-scoped, ministry exempt**, through the same resolver the record
  search uses.
- **Out-of-district resolves as 404, not 403.** Distinguishing "no such
  registrar" from "one you may not see" would confirm the id exists
  elsewhere, which is the thing the scope withholds. An unknown id answers
  identically, deliberately.

**Listing needs an oversight role; resolving one id does not.** Knowing who
holds an account is oversight. Putting a name to an id in a queue is what
makes that queue auditable by the person reading it, and gating it would
leave a facility registrar unable to tell who approved their own correction.

**No `AuditLog` row.** That table covers domain writes; reading a colleague's
name is not one, and `RequestLog` already records every `/api` call. The
record search is the deliberate exception, because searching *citizens* by
name is a surveillance act rather than a read.

A directory entry carries id, name, role, facility and district — never the
PIN hash, whose only legitimate destination is the credential bundle a device
caches, and never `ExternalSubjectId`, which identifies the account to
Keycloak rather than to anyone here.

`DistrictScopeResolver` was extracted in this PR and the record search moved
onto it. Two copies of "which district may this caller see" is precisely the
drift to avoid, and the way it shows up is one endpoint quietly ceasing to
enforce the boundary.

**Still unnamed, and worth its own work:** `DeviceAlertResponse` records
`acknowledgedAtUtc` and a note but not *who* acknowledged, so a district
cannot tell who said they were dealing with a dead tablet. And
`BirthRecordResponse` names nobody who registered the birth. Neither is W3,
but both are the same class of gap.

### Naming the two unnamed actors — PR #20

Both ids were stored from the start and neither was published, so a screen
could show that a birth was registered and an alert acknowledged without
naming a person for either.

- **`BirthRecordResponse`** now carries who filed the registration, and the
  record screen shows it.
- **`DeviceAlertResponse`** now carries who acknowledged the alert, in the
  queue as well as in the response to pressing the button. That was the
  sharper gap: **acknowledging is not resolving** (WS-F4) — it says "I am
  driving out there Thursday" — and an undertaking nobody is named for is one
  nobody can be asked about on Friday.

**The timestamp is named `ReceivedAtUtc`, not a registration time**, and that
is a correction rather than a preference. CLAUDE.md says of late registration
that the residual risk "is why both timestamps are stored" — but the device's
capture time is **not stored**. `BirthRegistrationService` computes it,
validates it against the birth date and the server clock, uses it to decide
lateness, and discards it. Only server receipt survives, so only server
receipt is published, under its own name.

#### Two findings, one of them serious

**The capture time is not stored.** The documented defence against a device
claiming a plausible-but-false capture time is having both timestamps to
compare afterwards. There is currently nothing to compare: a backdating
attempt that passes validation leaves no trace of what was claimed. Closing
it is a column on `BirthRecord` and a migration on both providers.

> Closed — see *The capture time, kept* below. It was two columns, not one:
> the window the record was judged against has to travel with the timestamp
> or the decision still cannot be reproduced.

**`BirthRecords → Registrars` cascades on delete.** Deleting a registrar
deletes every birth they ever filed. Nobody chose this — it is EF's default
for a required relationship — but in a register where an annulment keeps the
record, a duplicate keeps both, and the audit trail cannot be rewritten, it
is the one delete that was never intended. `ReferentialAction.Restrict` is
the obvious correction; it needs a migration on both providers.

The cascade is pinned down by a test rather than left to be rediscovered.

### The capture time, kept

`BirthRecord.RegisteredAtUtc` and `BirthRecord.StatutoryWindowDays`, plus both
timestamps on the record screen.

**The register was making a legal decision on a number it then threw away.**
The statutory window is measured to the moment a birth was captured on the
device, and failing it withholds the certificate until a district registrar
verifies evidence. The service computed that moment, bounds-checked it
against the birth date and the server clock, decided on it — and discarded it.
A family told their registration was late could not be shown why, because the
value the finding rested on existed nowhere, and recomputing it from the
arrival time gives a different answer for every record the offline tier has
ever produced.

**It was two columns, not one.** Storing the timestamp alone makes lateness
*recomputable*, and a recomputation against a changed law gives a confidently
wrong answer — the window is set in law and is configuration for exactly that
reason. So the window applied is stored with it. That is the same rule already
applied one layer out, where `BirthRegisteredEvent.WithinStatutoryWindow`
carries the decision rather than the timestamps; the registry itself had
neither. Note `LateRegistration.WindowDaysAtFiling` already kept the window —
but only for records that were late, which is the asymmetry: the on-time
majority, where a later dispute is *about* whether they were on time, kept
nothing.

**Nulls mean absence, never a default.** Rows written before the column keep
null rather than a backfill from `CreatedAtUtc`, which would assert that every
offline record was captured at the moment it arrived — the one claim that is
false for precisely the tier this system exists to serve. On an online
registration the two are equal, and that is a fact about the record rather
than a missing value; the distinction is the same one the dashboard makes
when it reports null and says why instead of reporting zero.

**The residual is audited at the one moment anyone can see it.** A device
claiming a capture time inside the window on a birth that arrived outside it
is the cheapest way to commit the backdating the late process exists to deter.
Nothing refuses it — a post genuinely out of contact for months is
indistinguishable, request by request, from a dishonest one, and refusing
would close the offline tier. It is only distinguishable *in aggregate*, which
needs the individual occurrences written down: `StatutoryWindowMetOnDeviceTime:{byDevice}/{byArrival}`.
One is a village post; every time is a finding. The negative is tested too —
an ordinary registration writes no such row, or the queue would be noise.

The screen shows both dates together and badges the gap, because "received 3
October" on a birth in June reads as a filing three months late, which is the
exact conclusion measuring to the device's clock was meant to prevent.

### Splitting the delay the dashboard could not separate

`BirthRegisteredEvent.RegisteredAtUtc`, `RegistrationFact.PublishedAtUtc`, and
a `RegistrationDelay` block on `GET /api/dashboard/summary`.

The previous change kept the device's registration time in the registry. It
did not travel, so the reporting side still saw one number where there are
two: **how long the family took to reach a registrar, and how long the record
then took to reach the centre.** One is answered by an outreach campaign, the
other by a mast, and `TimeToConfirmation` — birth to a confirmed BRN — spans
both and separates neither.

For a hospital that hardly matters. For the tier this platform exists to serve
the second term can be most of the total, so reading the combined figure as
the first says families near a village post are slow to register when they are
not. That is a conclusion that redirects money to the wrong intervention.

**The read model had one name for two instants.** `RegistrationFact.RegisteredAtUtc`
held the *publish* time while `BirthRecord.RegisteredAtUtc` held the device's —
values that can be three weeks apart. Renamed to `PublishedAtUtc`. The
existing dashboard fixture proved why it mattered: after the rename it still
compiled and still passed, now silently setting the device time and leaving
the publish time at year 0001. Every new median would have read a confident
zero.

**The consumer refuses to start on a stale read model.** The projection is
created with `EnsureCreated`, which does nothing to a database that already
exists — so this rename would have left a deployed consumer starting cleanly,
consuming happily, and failing on the first dashboard query with `no such
column`, which reads as a broken deployment rather than a projection needing a
rebuild. `ReadModelSchema.EnsureUsable` refuses at startup and names the
remedy instead, the same rule as an unrecognised `Database:Provider`. It
proved itself immediately by failing on this machine's own stale store.

Nulls stay nulls throughout: events predating the field are counted as
`NotMeasurable` and excluded, never folded in as a zero-day delay, which would
report the offline tier getting faster the more of its history predated the
measurement.

#### Found, not fixed

**The consumer's OpenAPI document widens every integer to
`["integer","string"]`** — 26 fields before this change, 30 after. The API has
none, because `NumberSchemaTransformer` narrows them there; the consumer never
got that transformer, so its generated client types `measured` as
`number | string` and every median as `null | number | string`. Fixing it is
registering an existing transformer on a second service, but it retypes the
whole consumer contract, so it wants its own PR and its own before/after diff
rather than riding along inside a feature.

> Closed — see *Numbers are numbers* below. It did **not** want the
> transformer: the consumer has no request bodies, so it needed to stop being
> lenient rather than to describe its leniency away.

### Numbers are numbers

`JsonNumberHandling.Strict` on the consumer's HTTP JSON options, and a test
pinning both contracts.

The plan above assumed the fix was to register the API's
`NumberSchemaTransformer` on a second service. **Checking the premise first
changed the answer**, which is now the third time that has paid here.

The widening exists because ASP.NET's web JSON defaults set
`AllowReadingFromString`, so the serializer accepts `"42"` as well as `42`
*on the way in*. One component schema describes both directions, so every
number the service returns inherits it.

**Every consumer endpoint is a GET.** It has no request bodies at all, so the
leniency buys it nothing — there is nothing to be lenient about. The API's
transformer is the right answer for the API, which takes bodies and chooses to
be forgiving; copying it here would have corrected the description of a
leniency this service does not want and cannot use. Turning it off instead
makes the document state what the service does rather than understate it.

Checkably inert at runtime, which is why this is safe: `AllowReadingFromString`
affects reading, this service only ever writes through the HTTP JSON pipeline,
and the Kafka projector deserialises with `JsonSerializer.Deserialize` passing
no options — so it uses `JsonSerializerOptions.Default` and never sees the
setting.

Result: 184 lines out of the document, 42 in. All 30 widenings and every
generated `pattern` gone; the client types `measured` as `number` and the
medians as `null | number`. **Nullability survives** — `["null","number"]`, not
`number` — which matters, because an indicator reporting null rather than a
misleading zero is the rule this projection is built on, and it has to reach
the contract to reach a dashboard.

Pinned by `OpenApiNumberTypingTests` over both documents rather than left to
the CI drift check. The drift check fails on any contract change, so it
catches a regression only if whoever reads the diff recognises the widening as
a fault — and **it was rationalised rather than diagnosed once already in this
repo**, in PR #10. The test was proved non-vacuous by running it against the
pre-fix document, where it fails.

### The record screen, and what it could not say

`BirthRecordResponse` gains `ProvisionalIdentifier` and a `Certificate` state
block, `GET` finally populates `LateRegistration`, and the record screen
becomes a detail view with a corrections tab.

**The late-registration badge shipped in PR #14 could never appear.** The
field was on the response but populated only by the *registration* endpoint,
never by the lookup — so a record opened afterwards showed no sign it had been
filed late and no sign its certificate was being withheld pending
verification. That is exactly the message CLAUDE.md says a family must not be
sent away without, and the screen was structurally incapable of showing it.
Found by checking the plan's premise rather than trusting it, which is now the
fourth time that has paid.

**The provisional identifier was retained and never published.** Lookup has
always resolved on it — a family may still be holding the slip a device
printed — but the response never said the record had one. So a clerk handed a
`PROV-` slip found the record and saw only a BRN, with nothing confirming the
two were the same registration. The one moment the retention exists for was
the one moment it was invisible.

**Certificate state travels on the record, not beside it.** A certificate is
valid only if signed *and* not revoked, and the paper in a family's hands does
not change when the register is corrected. Fetched separately, a screen could
render "issued 3 March" without the withdrawal and state something false about
a legal document. One payload makes that particular mistake impossible. The QR
payload and signature are deliberately **not** included: those are what
actually verifies a certificate, and they stay behind the certificate
endpoint's own policy.

The corrections tab lists refused and still-pending changes alongside applied
ones. A change someone proposed and a reviewer turned down is part of a
record's history too, and is often the part a dispute turns on; a queued change
shown as applied would misdescribe the register while it waits.

Guarded at both halves, which is the split worth being explicit about: the API
tests prove the lookup *returns* the late registration, the component tests
prove the screen *renders* the warning. Neither alone would have caught the
bug.

### W2 as built

`GET /api/facilities` and `GET /api/facilities/{facilityId}`, plus the
`/facilities` screen. PR #19.

Premise checked first, per the lesson W3 taught: two endpoints already took a
`facilityId` — granting a BRN block and fetching device credentials — but
nothing could produce one or say anything about a facility, and there was no
facility schema at all. The gap was real.

#### The point is block exhaustion, not a directory

A facility that runs out of its granted BRN block starts issuing `PROV-`
identifiers (draft 6.3). The birth is still registered, but the family leaves
with a slip rather than a certificate and the record waits for a central act
before it can be certificated. **Seeing that coming is the difference between
granting a block and explaining to a district why forty families are holding
provisional paper.**

#### "Low" is not one number

The same reasoning as WS-F4's device-silence thresholds, and deliberately the
same shape so a reader who understands one understands the other:

| Connectivity | Warn below |
|---|---|
| `AlwaysOn` | 50 — can ask for more this afternoon |
| `Intermittent` | 200 — a few days of registrations |
| `OfflineFirst` | 500 — the buffer must cover the whole offline stretch |

A hospital with 100 numbers left is fine. A village post with 100 left, a
fortnight from its next connection, is already in trouble. Applying the
hospital's threshold to the post is how the post runs out; applying the
post's to the hospital fills a district's screen with warnings about
facilities working exactly as designed — and a queue that is mostly noise
stops being read.

Three states rather than a percentage, because the action differs at each and
a percentage invites the reader to invent their own threshold. **The
threshold is published beside the count**, so a post flagged at 400 and a
hospital clear at 60 does not look arbitrary.

#### Two details that fail quietly

- **The remaining count is inclusive of the end and exclusive of next**, the
  same arithmetic `BrnReconciler` uses. An off-by-one here reports a number
  the facility cannot actually issue, and the first symptom is a device
  handing out a BRN the centre then refuses.
- **A facility never granted a block has all three fields at zero**, which
  that arithmetic alone reports as one number available. It has none.

#### Verified against the running stack

All three states, by temporarily moving one dev facility's block and
restoring it exactly:

| Remaining | Shown |
|---|---|
| 89,499 | "in hand" |
| 300 | "grant a block" |
| 0 | "issuing provisional numbers" |

Scope held — a Central district officer saw only the Central facility, never
Lusaka. Two grammar faults were caught and fixed in passing: "1 facilities",
and "1 of 1 facility need".

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

- [x] **W5** CORS on `NCBRS.Api` and `NCBRS.Consumer`; origins from configuration, no wildcard, credentials off — PR #2
- [x] **W6** `ncbrs-web` public client: standard flow on, direct access grants **off**, PKCE `S256`, exact redirect URIs — PR #2
- [x] Realm import verified from a recreated container
- [x] **W7** Cursor paging on the four review queues — PR #3
- [x] OpenAPI audit — see below
- [x] **W8** Give `NCBRS.Consumer` an OpenAPI document (found by the audit) — PR #5
- [x] **W9** Authenticate the consumer's reporting endpoints — they served national vital statistics anonymously — PR #6

#### OpenAPI audit — result

Audited against the running service, not read off the source.

**`NCBRS.Api` passes.** Nothing needed fixing:

| Check | Result |
|---|---|
| Coverage | 36 of 36 endpoints documented |
| `{meta, data}` envelope | Documented on every response, not just the inner type |
| Enums | 18 as strings, 0 as integers |
| Paging | `Page<T>` schemas generated for all four queues; `limit` and `after` documented |
| Correlation headers | `X-Transaction-Id` and `X-Client-Id` documented as parameters |
| Security | `bearer` scheme declared and applied globally |
| Errors | 400/401/403/404/409 documented per operation |

The envelope was the one most likely to be wrong — `[ProducesResponseType]`
declares the *inner* type while `MetaEnvelopeFilter` wraps the response at
runtime, so the schema could easily have described a shape the API never
returns. It does not; the document is accurate.

**`NCBRS.Consumer` has no OpenAPI document at all.** That is W8. Five
endpoints are undocumented, and four of them are what Phase 6's dashboard is
built on:

```
/health
/api/dashboard/summary
/api/dashboard/districts
/api/dashboard/devices/silent
/api/exports/dhis2
```

This matters more than it looks. §2 decided React over Blazor on the strength
of a generated client, and a generated client can only cover the half of the
surface that has a document. The dashboard would otherwise be hand-written
types — precisely the drift the decision was meant to prevent, appearing in
the part of the system whose numbers a Ministry acts on.

Closing it needs the `Microsoft.AspNetCore.OpenApi` package added to
`NCBRS.Consumer`. It is already used by `NCBRS.Api`, but adding a package to
a project is a dependency change and needs sign-off.

### Phase 1 — Skeleton that proves the risky parts
*Ends with one screen. The point is not the screen; it is that auth, the
envelope, error handling and the generated client are proven together before
anything is built on them.*

**Scaffold** — PR #7
- [x] `npm create vite@latest web -- --template react-ts` under a new top-level `web/` directory
- [x] `npm install tailwindcss @tailwindcss/vite` and replace `src/index.css` with `@import "tailwindcss";`
- [x] The `@/*` → `./src/*` path alias in **both** `tsconfig.json` and `tsconfig.app.json`
- [x] Add the matching `resolve.alias` for `@` and the `tailwindcss()` plugin to `vite.config.ts` — the alias must be set in both places or the editor and the build disagree
- [x] `npx shadcn@latest init`, then add components as needed — 14 installed: `button` `card` `input` `label` `table` `dialog` `dropdown-menu` `badge` `separator` `skeleton` `sonner` `empty` `spinner` `alert`
- [x] Commit `components.json` and treat `src/components/ui/` as vendored source per §2
- [x] A CI job running lint, typecheck and build

Four things about the live tooling differ from what this plan assumed when
it was written, and each changes an instruction above rather than the intent
behind it:

- **The Vite template ships `oxlint`, not ESLint.** So the ESLint step is
  gone and `npm run lint` is oxlint, with `--deny-warnings` in CI. **No
  Prettier** — it is not in the template and adding a formatter is a
  dependency change of its own. Worth revisiting before several people are
  editing this at once, because a formatting disagreement shows up as noise
  in every diff.
- **`@types/node` is already in the template**, so that install is a no-op.
- **`baseUrl` is gone.** shadcn's install guide still pairs one with
  `paths`, but TypeScript 6 deprecates it (TS5101) and it fails the build
  today. `paths` alone resolves relative to the config file, which is what
  was wanted anyway.
- **shadcn's CLI now asks for a *base* and a *preset*.** Chosen: `radix` and
  `nova` (Lucide icons, Geist). Note it no longer generates a `cn` helper —
  `src/lib/utils.ts` re-exports one from the `cn` package
  (`github.com/shadcn-ui/cn`, the same org), replacing `clsx` +
  `tailwind-merge`.

`form` is deliberately **not** installed yet: it pulls `react-hook-form` and
`zod`, and nothing in this PR uses them. It arrives with Phase 2's
registration form, where the dependency is justified by something that
needs it.

**Contract**
- [x] Align both services on one OpenAPI generator — PR #8
- [x] Give every controller action an `operationId` — verb-first, matching the consumer — PR #9
- [x] Generate the typed API client from the OpenAPI document into `src/api/generated/` — PR #10
- [x] Wire generation into CI so a drifting contract fails the build rather than production — PR #10
- [x] Wrap the request/response envelope once, centrally (`{envelope, data}` out, `{meta, data}` back), including the transaction id header — PR #10

#### How the contract is kept honest — option C

The documents are **committed** under `web/openapi/`, and the **build
regenerates them** from the real endpoints: both services reference
`Microsoft.Extensions.ApiDescription.Server` and point
`OpenApiDocumentsDirectory` there. CI builds, regenerates the TypeScript, and
fails on any difference.

That combination is the point. A committed document alone is a reviewable
artifact — a contract change shows up as a diff in the PR that causes it,
which matters where response shapes carry legal meaning — but it can go
stale. Regenerating from a *running* service cannot go stale but needs both
services and their databases booted in CI, which is slow and adds failures
unrelated to the change under review. Building the document from the code
gets the artifact and the accuracy, and boots nothing.

Verified both directions, because a drift check that never fires is worse
than none: a clean tree produces no diff, and adding one response code to
`/health` produced a 19-line diff across the document and the generated
types.

**Generator: `openapi-typescript` + `openapi-fetch`, not `@hey-api/openapi-ts`.**
hey-api generates operationId-named SDK functions, which would have suited
the naming work better, but no version of it is currently free of advisories:
0.99 carries four high-severity `js-yaml` issues transitively, and 0.97
trades them for a prototype-pollution one. `openapi-typescript` audits clean,
generates types only — no generated runtime code to churn — and still keys
its `operations` interface by `operationId`, so the names earn their place.
Call sites go through paths (`client.GET('/api/BirthRecords/{brn}')`) rather
than named methods; the envelope and headers live in `src/api/client.ts`,
which is where the plan wanted them anyway.

**One wart: `web/.npmrc` sets `legacy-peer-deps=true`.** openapi-typescript
7.13 still declares `peer typescript@^5.x` while this project is on
TypeScript 6. The range is stale rather than accurate — generation works and
the output typechecks under `tsc 6`, which CI proves on every run — but the
setting relaxes peer checking for the whole tree, not just that package, and
npm offers no narrower waiver. Remove it when openapi-typescript widens its
range.

Also fixed here: **the consumer's document declared no security scheme at
all**, while all four reporting endpoints sit behind `ncbrs-reporting` (W9).
A client generated from it would have sent no token and 401'd on every
dashboard call — the same class of omission as the API's, since the built-in
generator infers nothing about authentication. `ReportingSecurityTransformer`
reads each endpoint's own authorization metadata, so `/health` stays open and
endpoints added later are described correctly without anyone remembering.

#### Generator alignment — result

`NCBRS.Api` emitted OpenAPI **3.0.4** through Swashbuckle while
`NCBRS.Consumer` emitted **3.1.1** through the built-in generator. Both now
use the built-in one and emit 3.1.1, so a single client generator reads both
documents. The API keeps `Swashbuckle.AspNetCore.SwaggerUI` for the explorer
itself, pointed at `/openapi/v1.json` — what a developer explores and what a
client is generated from are now the same document.

Proved by capturing the Swashbuckle document first and diffing a reduction of
both against each other: 36 operations, every parameter, every response code,
every `{meta, data}` envelope and the security scheme came through
unchanged.

Four things the swap broke or exposed, none of which the manual audit could
have caught:

1. **`/openapi/v1.json` returned 401.** `UseSwagger()` was middleware and ran
   ahead of authorization; `MapOpenApi()` maps an endpoint, which the global
   `FallbackPolicy` catches. The document was unreachable — to the UI, to a
   developer and to the client generator. Needs `.AllowAnonymous()`.
2. **Every enum became a bare `integer`.** MVC serializes through
   `Mvc.JsonOptions`; the built-in generator describes types through
   `Http.Json.JsonOptions` and consults nothing else, so it could not see
   `JsonStringEnumConverter`. `Sex` would have documented as a number with no
   allowed values while the API sends `"Female"` — the integer-code mix-up
   CLAUDE.md calls out, made permanent in every generated call site.
3. **204 responses carried a body schema.** Swashbuckle left them empty; a
   generated client would wait to parse a payload that never arrives.
4. **Nullable enums lost their type.** 3.1 folds `null` into the enum values,
   and the generator then omitted `type` entirely. Now typed
   `["string","null"]`, which keeps `NotStated` ("declined to say") distinct
   from `null` ("never asked") — the draft 6.5.1 distinction — in the
   generated types. This is the one place the new document is *better* than
   the old: 3.0 could not express it and Swashbuckle claimed those three
   fields were always strings.

**Resolved in PR #9.** All 36 operations are named, verb-first
(`RegisterBirth`, `GetBirthRecord`, `IssueCertificate`), matching the
consumer's existing `GetDashboardSummary` so the generated client reads the
same way across both services.

Two details worth keeping:

- **`[EndpointName]` does not work on MVC actions** — it produced no
  `operationId` at all. The name has to go on the HTTP attribute itself,
  `[HttpGet("{brn}", Name = "GetBirthRecord")]`, which is what ApiExplorer
  reads.
- **`GetCertificateSigningKeys` is plural although the route says
  `signing-key`.** The endpoint publishes the whole set, retired keys
  included, and a client method called `GetSigningKey` would invite callers
  to assume there is only ever one — which is exactly the assumption that
  makes a device reject every certificate issued before the last rotation.

Naming the routes makes them *named* routes, which put the two
`CreatedAtAction` calls at risk of losing their `Location` header. The test
suite cannot see this: it constructs controllers directly and asserts on the
status code of the result object, so no URL is ever generated. Verified
instead with a real request against the running service — `POST /api/devices`
returned `201` with `Location: .../api/devices/{deviceId}`.

**Auth (§4)** — PR #11
- [x] `oidc-client-ts` + `react-oidc-context` against `ncbrs-web`; authorization code + PKCE
- [x] Access token in memory only; refresh via refresh token, never an iframe
- [x] Login, logout, post-logout redirect, and the callback route
- [x] Parse `realm_access.roles`; expose them through one module that mirrors the API's policy names
- [x] Route guards for navigation, plus a graceful 403 screen — the server is the control, the UI is a courtesy

#### What "in memory only" actually means

**Tokens in memory; the PKCE handshake state in `sessionStorage`.** The split
is not a compromise, it is forced: the `code_verifier` and `state` are created
before the redirect to Keycloak and needed after the redirect back, and a
redirect is a full page unload. In-memory state cannot survive it, so an
all-in-memory configuration fails *every* sign-in with "No matching state
found in storage" — which is how this was found, by trying it.

The exposure is much smaller than the tokens'. A verifier is single-use, lives
for the seconds of one round trip, authorizes nothing and identifies nobody.
The long-lived credential never touches storage — verified in the browser:
`localStorage` and `sessionStorage` both empty after sign-in, no JWT anywhere.

**A reload does not sign the user out.** There is no token in memory, so the
app redirects to Keycloak — which still holds the SSO session cookie and
bounces straight back with no prompt. What is lost is in-page state, which is
a draft-persistence problem for Phase 2, not a reason to weaken this.

**This is not the strongest pattern available.** The browser-apps BCP
recommends a backend-for-frontend, where tokens never reach JavaScript at all;
with tokens in memory an XSS bug can still act as the user for the life of the
page, it just cannot exfiltrate a credential for later use elsewhere. A BFF
needs a server component this architecture does not have. Moving to one later
changes `oidc.ts` and `client.ts`, not every call site.

#### Every screen here is registry components (§2)

The first draft of this PR hand-wrote a centring wrapper, a loading block and
a placeholder card. All three were replaced: `Empty` already centres, spaces
and balances its own text, so the wrapper was duplicating it, and the states
it exists for are exactly these.

| Screen | Components |
|---|---|
| Signing in / completing sign-in | `Empty` + `Spinner` |
| Sign-in failed | `Empty` + `Button` |
| 403 | `Empty` |
| Signed in | `Card` + `Item` + `Avatar` + `Badge` + `Button` |

`avatar` and `item` were added from the registry rather than composed by
hand. What remains hand-written is layout only — page height, centring, a
max-width — and one `AuthStatus` page frame, which exists because `Empty`
fills its parent rather than the viewport and something has to give it the
height. No control, icon or piece of chrome is bespoke.

#### Verified against the live realm, not a mock

Keycloak from `docker compose`, signing in as `district.officer`:

| | |
|---|---|
| Unauthenticated → Keycloak | ✅ |
| Code exchange → back to the app | ✅ |
| Roles from `realm_access` on the **access** token | ✅ `district-officer` |
| Reload → no prompt (SSO cookie) | ✅ |
| Tokens in web storage | ✅ none, anywhere |
| Deep link survives sign-in | ✅ `/annulments` preserved |
| 403 screen | ✅ "It needs ministry-admin. You are signed in as district-officer." |
| Sign-out ends the **Keycloak** session | ✅ next visit shows the login form |

Three bugs that only a real IdP would have shown, all fixed:

1. **No `/auth/callback` route.** The code exchange succeeded and the router
   matched nothing, so a successful sign-in rendered a blank page.
2. **`stateStore` in memory.** Every sign-in failed, as above.
3. **Deep links lost.** The callback navigated to `/` unconditionally, so a
   link to a specific record — exactly what one registrar sends another —
   would never open that record. The intended path now travels through the
   OIDC `state`, and is validated as a same-site path on the way back, since
   an unchecked value round-tripping through the browser is an open redirect.

**Not yet proven: a real API call.** Sign-in works, but nothing has yet sent
the token to `NCBRS.Api` — so CORS (W5) and the `ncbrs-api` audience mapper
(W9) are still only verified from the Keycloak side. The vertical slice in the
Shell PR is where that gets exercised, and it is the first place either could
fail.

**No web tests exist yet.** No framework is installed. The `returnTo`
open-redirect guard and `realmRolesFromToken` are pure functions handling
untrusted input and should have unit tests; that needs vitest, which is a
dependency decision of its own.

**Shell** — PR #12
- [x] App layout: navigation, header with signed-in user and role, sign-out
- [x] Role-aware navigation, so a facility registrar is not shown four empty review queues
- [x] Error surface that renders the API's `errors[]` against the right form fields
- [x] Loading, empty and failure states, decided once — `spinner`/`skeleton`, `empty` and `alert` from the registry, not written by hand (§2)
- [x] **Vertical slice: look up a BRN and display the record**

#### The slice proved five things at once

Nothing before this had sent a token to the API. One lookup exercises the
whole chain, and any link in it could have been wrong:

| | |
|---|---|
| CORS preflight (W5) | `OPTIONS /api/BirthRecords/100000 → 204` |
| Token + `ncbrs-api` audience (W9) | `GET → 200`, not 401 |
| `{meta, data}` envelope | unwrapped to the record |
| Generated types | rendered without a hand-written interface |
| String enums (PR #8) | `Sex` shows **"Female"**, not `1` |

That last row is the one worth keeping. Had the enum fix not landed, this
screen would have displayed a number, and the fault would have been three
layers away in a generator's reading of `Http.Json.JsonOptions`.

Navigation is derived from `src/shell/navigation.ts`, which names the policy
gating each destination, so the sidebar and the route guards read the same
list. Two lists would drift, and the drift is silent in both directions: a
link to a page that refuses the user, or a page the user is entitled to that
never appears, leaving them to conclude the system cannot do it. Verified
live — signed in as a district officer, `/review/annulments` is absent from
the sidebar entirely and the route answers with the 403 screen.

Unbuilt destinations are routed and gated anyway, marked "soon". The shape of
the system is legible before all of it exists, and the guards are exercised
against the real policy list rather than one example.

#### Tests

vitest, jsdom and Testing Library, with **36 tests** over the pure logic that
handles untrusted input:

- `returnTo` — the open-redirect guard. Absolute, protocol-relative,
  backslash-normalised and `javascript:` values all refused.
- `realmRolesFromToken` — malformed tokens yield no roles rather than
  throwing, and non-string entries are dropped.
- `roles` — the policy table, asserting review stays out of the filer's hands
  and annulment stays at the ministry.

Writing them found a bug in the test rather than the code: the first helper
base64'd Latin-1 while `decodePayload` correctly decodes UTF-8, which is what
Keycloak emits. Worth recording because the failure looked like a production
bug and was not.

Components under `src/components/ui/` and `src/api/generated/` are excluded
from coverage: the first is upstream's, the second is regenerated and already
checked by the contract job.

### Phase 2 — The register
- [x] **W1** Record search backend: district-scoped from the token, ministry exempt, every search audited — PR #13

#### W1 as built

`GET /api/birthrecords/search` — `SearchBirthRecords` — by name, date range,
facility and status, paged with the same cursor contract as the review
queues (W7).

Deliberately a **separate controller** from `BirthRecordsController`. That one
answers "show me the record with this number", which a family holding a
certificate is entitled to ask. This one answers "which records match this
name", which is a surveillance capability. Side by side, the second would
have inherited the first's openness.

Four controls, each of which had to be built in rather than added later:

- **Scope comes from the token.** The caller's district is resolved through
  their registrar record; a `districtId` naming a different one is **refused,
  not narrowed**. Narrowing silently would answer an empty page, which the
  caller reads as "no such child in that district" — a false statement about
  a district they were never allowed to ask about.
- **Only `ministry-admin` is exempt.** A district officer is not: overseeing
  several facilities is not overseeing several districts.
- **A blank search is refused.** A name of at least 2 characters or a date
  range is required; facility or status alone is still "every birth at this
  clinic". Leaving the form empty must not read a district out of the
  register.
- **An account with no registrar record cannot search at all**, whatever
  realm role it holds. There would be nobody for the trail to name, and an
  unattributable search is the one thing this endpoint must not permit.

The audit row records **what was looked for**, not merely that a search
happened: criteria, rows returned and scoped total, against
`EntityType = "BirthRecordSearch"` and `EntityId` = the district (or
`national`). Twelve rows saying "a search occurred" cannot distinguish a
registrar helping one family from someone enumerating a district. A search
matching nothing is audited too — repeated fruitless searches for a surname
are exactly what fishing looks like.

Two costs accepted knowingly:

- **The trail now holds names of people who may not be in the register.**
  Unavoidable: an audit that cannot say what was looked for cannot
  distinguish use from misuse.
- **A name search is a scan.** The district filter is what keeps it
  survivable at national volume, which is one more reason the scope is not
  optional. An index on the child's name is the obvious next step if this
  gets slow, and should be measured before it is added.

A result carries only what identifies the right child — BRN, name, date, sex,
status, facility, district, provisional identifier. No parents, no weights: a
result list that carried them would spread them across every search that
happened to match.

- [x] Search UI and results — PR #14

#### The search screen

`/records/search`, a **separate destination** from the BRN lookup rather than
a mode of it. Looking up a number a family is holding and searching the
register by name are different acts under different rules — the second is
confined to the caller's district and written to the audit trail. One box
that quietly switched between them would hide that from the person it applies
to.

Two things the screen does because of how W1 refuses:

- **The scope is stated before anyone searches**, not after a disappointing
  result. A registrar who does not know results stop at the district boundary
  reads an empty page as "this child is not registered" — which for a family
  who moved is exactly wrong.
- **A 403 is explained as the boundary, not rendered as an empty result.**
  The API refuses a cross-district search outright rather than narrowing it,
  precisely so nobody is told "no such child" about a district they were
  never allowed to ask about. Swallowing that into an empty table would undo
  the refusal.

The total is shown as "N of M **in your district**", never a bare M.

Verified against the running stack, signed in as a district officer: a search
for a name present in two districts returned only the three records in the
caller's own, and the trail recorded
`Search:name=Amara;returned=3;total=3` against `D-CENTRAL-07`. A search with
no criteria was refused and wrote **no** audit row.

Two bugs found and fixed while verifying, neither visible from the code:

- **A stale breadcrumb.** Both crumbs keyed on the same `to`, so React could
  not tell them apart across a route change and kept one from the previous
  page. Only reproducible by navigating between two pages in the same group.
- **`[Produces("application/json")]` was missing** on the search controller,
  which every other controller carries. Without it ApiExplorer advertised
  `text/plain` and `text/json` as well — content types the endpoint never
  returns, and which a client generator picks from.

#### Contract regression fixed here too

The generator swap in PR #8 quietly widened **35 integer properties** across
the API to `["integer","string"]`. ASP.NET's web JSON defaults set
`AllowReadingFromString`, so the serializer will accept `"42"` on input; the
generator reported that faithfully, and because one component schema
describes both directions, every integer the API *returns* inherited it. The
typed client had `Page.total` and every error `status` as `number | string`.

Swashbuckle documented all 41 as plain integers. `NumberSchemaTransformer`
restores that. The document then understates what a request will tolerate,
which is the right direction to be wrong in: a generated client sends
numbers, the API accepts numbers, and nothing advertises a leniency callers
should not rely on.

This was visible in PR #10 — `errors.ts` handled `status` as either — and was
rationalised as JSON round-tripping rather than investigated. It was a
regression.
- [x] Record detail: identity, status, certificate state, provisional identifier, annulment block — PR pending
- [x] Amendment history timeline, showing previous values — PR pending
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
- [x] **W2** Facilities list, BRN block state, low-block warning — PR #19
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
