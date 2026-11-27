# Roles, permissions and creating users

A guide for whoever sets up accounts: which roles exist, what each may do and
where, and the steps to create each kind of user.

The source of truth is code, and this page restates it:
- `NcbrsRoles.Permissions` (`src/NCBRS.Core/Web/NcbrsRoles.cs`), the table both
  services build their policies from;
- `keycloak/ncbrs-realm.json`, the dev realm and the reference for production.

If this page and the code disagree, the code is right. Correct this page.

---

## 1. Every user lives in two places

| Place | What it holds | Who sets it |
|---|---|---|
| **Keycloak** (identity) | Username, password, one-time code, **one realm role**, and for county staff **one county group** `/counties/<p-code>` | Whoever administers the realm |
| **The registry** (NCBRS) | A **registrar** record binding that account to **a facility** and a role | A district officer or the system admin, from the web site's waiting list |

An account that exists only in Keycloak can sign in but can do nothing: the
registry refuses every write from an account it has no registrar for. The
account **declares itself** by signing in once, which puts it on the waiting
list ("Registrars → Waiting to be added"). Nobody copies ids out of Keycloak.

---

## 2. The RBAC tree

```
NCBRS realm
│
├── Facility roles ─ scope: OWN FACILITY ─ no one-time code ─ tablet holds an offline token (60 d idle / 180 d max)
│   │
│   ├── facility-registrar            (hospital / health-centre registrar, midwife)
│   │   ├── CanRegisterBirths         register, correct, sync, USB transfer upload, outcomes,
│   │   │                             maternal statistics, certificate hand-over, BRN blocks,
│   │   │                             certificates to hand out, facility operations, report a problem
│   │   ├── CanIssueCertificates      issue and reprint certificates
│   │   └── CanReadRecords            find by number, search, a record's history
│   │
│   └── community-health-worker       (village post)
│       ├── CanRegisterBirths         (as above, including recording a hand-over)
│       └── CanReadRecords
│           ✗ no certificates          decision D1: a CHW records births, a registrar certifies
│
├── County role ─ scope: OWN COUNTY (Keycloak county group = facility's county) ─ one-time code required
│   │
│   └── district-officer
│       ├── CanRegisterBirths
│       ├── CanIssueCertificates
│       ├── CanReadRecords
│       ├── CanApproveAmendments      not their own submissions
│       ├── CanApproveLateRegistrations   not ones they filed
│       ├── CanReviewDuplicates       both records must be in their county
│       ├── CanReadAuditTrail
│       ├── CanReadReporting          their county's dashboard only
│       ├── CanEnrolDevices           tablets, alerts, problem reports, staff directory
│       └── CanManageRegistrars       FACILITY STAFF ONLY: bind, withdraw, reset PIN
│
└── Ministry roles ─ scope: NATIONAL ─ one-time code required ─ decision D2 split the old ministry-admin in two
    │
    ├── registrar-general             (the records)
    │   ├── CanRegisterBirths
    │   ├── CanIssueCertificates
    │   ├── CanReadRecords
    │   ├── CanApproveAmendments
    │   ├── CanApproveLateRegistrations
    │   ├── CanReviewDuplicates
    │   ├── CanAnnulRegistrations     ← only this role
    │   ├── CanReadAuditTrail
    │   ├── CanReadReporting          whole country, or any one county
    │   └── CanExportReporting        ← only this role (DHIS2)
    │       ✗ no facilities, areas, devices or user management
    │
    └── system-admin                  (the system)
        ├── CanManageFacilities       ← only this role: facilities, office codes, CSV import, areas
        ├── CanManageRegistrars       ANY ROLE, anywhere
        └── CanEnrolDevices
            ✗ sees no birth record, registers nothing, no reporting

Helper roles (never assign directly; they come with the roles above)
├── otp-required      composite of district-officer, registrar-general, system-admin
└── offline_access    composite of facility-registrar, community-health-worker

Open to everyone
├── Anonymous         verify a certificate, revocation list, offline bundle, signing keys, /health
└── Any signed-in     facility list, administrative areas, registration rules, own PIN, /api/me, a colleague by id
```

---

## 3. Permission matrix

| Permission | facility-registrar | community-health-worker | district-officer | registrar-general | system-admin |
|---|:-:|:-:|:-:|:-:|:-:|
| CanRegisterBirths | ✓ | ✓ | ✓ | ✓ | |
| CanIssueCertificates | ✓ | | ✓ | ✓ | |
| CanReadRecords | ✓ | ✓ | ✓ | ✓ | |
| CanReviewDuplicates | | | ✓ | ✓ | |
| CanApproveAmendments | | | ✓ | ✓ | |
| CanApproveLateRegistrations | | | ✓ | ✓ | |
| CanAnnulRegistrations | | | | ✓ | |
| CanReadAuditTrail | | | ✓ | ✓ | |
| CanEnrolDevices | | | ✓ | | ✓ |
| CanManageRegistrars | | | ✓ (facility staff) | | ✓ (any role) |
| CanManageFacilities | | | | | ✓ |
| CanReadReporting | | | ✓ (own county) | ✓ (national) | |
| CanExportReporting | | | | ✓ | |
| **Scope** | own facility | own facility | own county | national | national |
| **One-time code** | no | no | **yes** | **yes** | **yes** |

**A permission says *what*; the scope says *where*.** A district officer holding
`CanApproveAmendments` still cannot approve one in another county, and asking
about another county is refused, never quietly narrowed.

---

## 4. What each role sees on the web site

| Menu item | Policy | facility-registrar | CHW | district-officer | registrar-general | system-admin |
|---|---|:-:|:-:|:-:|:-:|:-:|
| Find by number, Search the register | CanReadRecords | ✓ | ✓ | ✓ | ✓ | |
| Register a birth, Upload a transfer file | CanRegisterBirths | ✓ | ✓ | ✓ | ✓ | |
| Certificates to hand out, Facility operations | CanRegisterBirths | ✓ | ✓ | ✓ | ✓ | |
| Amendments | CanApproveAmendments | | | ✓ | ✓ | |
| Duplicates | CanReviewDuplicates | | | ✓ | ✓ | |
| Late registrations | CanApproveLateRegistrations | | | ✓ | ✓ | |
| Annulments | CanAnnulRegistrations | | | | ✓ | |
| Facilities, Administrative areas | (signed in) to view; CanManageFacilities to add | view | view | view | view | **add** |
| Registrars, Devices, Device alerts, Problem reports | CanEnrolDevices | | | ✓ | | ✓ |
| Audit trail | CanReadAuditTrail | | | ✓ | ✓ | |
| Dashboard | CanReadReporting | | | ✓ | ✓ | |
| DHIS2 export | CanExportReporting | | | | ✓ | |
| Verify a certificate, Revocation list | anyone | ✓ | ✓ | ✓ | ✓ | ✓ |

The system admin lands on **Facilities**; everyone else lands on the record lookup.

---

## 5. Who may add whom

| To add a… | In Keycloak, give | Bound in the registry by | Needs a county group? |
|---|---|---|---|
| facility-registrar | `facility-registrar` | their county's district officer, or the system admin | yes (the facility's county) |
| community-health-worker | `community-health-worker` | their county's district officer, or the system admin | yes (the facility's county) |
| district-officer | `district-officer` | **system admin only** | **required**: binding is refused without one |
| registrar-general | `registrar-general` | **system admin only** | no |
| system-admin | `system-admin` | **another system admin** | no |

Why the limits:
- **A district officer binds facility staff only.** An officer able to create
  officers could widen their own oversight.
- **The role must be held in Keycloak.** The registry offers only roles the
  account's own token carries.
- **The county group must match the facility's county.** If it doesn't, the
  binding is refused. A district officer whose group later disagrees with their
  facility is refused on every call until one is corrected.
- **Nobody withdraws themselves**, and an officer withdraws facility staff only.

---

## 6. Creating a user, step by step

### 6.1 In Keycloak (every role)

Admin console → realm `ncbrs` → **Users → Add user**.

1. **Username:** the one they'll sign in with (it is also what a CSV import
   names them by). Fill in first and last name; the registry shows them as the
   person's name. Email is optional.
2. **Credentials → Set password.** Tick *Temporary* so they choose their own at
   first sign-in. The policy is 12–128 characters, not the username or email,
   and none of their last 5.
3. **Role mapping → Assign role → Filter by realm roles.** Assign **exactly
   one** of the five roles. Never assign `otp-required` or `offline_access`
   directly; they come with the role.
4. **Groups → Join group → `counties` → the county's p-code** (for example
   `SS0101` for Juba). Do this for facility staff and district officers, and
   **only one** county. Ministry accounts get no county group.
   If the county's group doesn't exist yet, create it under `/counties`, named
   by the p-code exactly as the registry uses it (RUNBOOK, "Deployment: county
   groups in Keycloak").

### 6.2 The person signs in once

They open the registry web site and sign in.
- **District officer and both Ministry roles:** Keycloak first sends them to
  "Mobile Authenticator Setup". Have an authenticator app on their phone
  (FreeOTP, Google Authenticator or Microsoft Authenticator). From then on
  every sign-in asks for a 6-digit code.
- They see "Your account is waiting to be added". That sign-in has put them on
  the waiting list.

### 6.3 Bind them in the registry

Web site → **Registrars → Waiting to be added → Add**. Choose:
- **the facility:** where they work. A district officer sees only their
  county's facilities. Ministry accounts are bound at a Ministry facility
  (headquarters), which records where they sit;
- **the role:** only roles the account holds in Keycloak are offered.

They can work straight away (after signing out and in again if their page was
open).

**Many at once (training day):** everyone signs in once, then **Add from a
file** on the waiting-accounts card takes a CSV with the columns
`username, facility, role` and, optionally, `display_name`.
- The facility is its office code or its name.
- The file is checked whole first, then applied whole or not at all.
- Each row is held to the same limits as adding one person.

### 6.4 Role-specific finishing steps

| Role | Then |
|---|---|
| facility-registrar, CHW | Set a **PIN** on a tablet with signal ("Set or change my PIN"), 6–12 digits. Each tablet at the facility picks it up at its next sync, and any of them can then be unlocked with it. A district officer must have **handed the tablet over** (enrolled it) first. |
| district-officer | Check the dashboard opens: it confirms the county group. They'll type a one-time code on each tablet they hand over. |
| registrar-general | Nothing more. |
| system-admin | Nothing more. Keep at least **two**, so one can bind or withdraw the other. |

### 6.5 A District node (a service account, not a person)

Each District node has its own confidential Keycloak client, with client
credentials only (the dev realm's `ncbrs-district` is the reference).
1. Turn on its service account and give it `district-officer` plus the county
   group of the county it serves.
2. Bind a registrar to that service account's subject at a facility in that
   county, so every batch it forwards is credited to the node.

See RUNBOOK, "Deployment: Keycloak realm".

---

## 7. Day-to-day account care

| Situation | Who | What to do |
|---|---|---|
| **Someone leaves** | district officer (facility staff) or system admin (anyone) | Registrars → **Withdraw**, with a reason. Then **disable** the Keycloak account. Their PIN stops unlocking each tablet at that tablet's next sync. They stay in the directory, because their records name them. |
| **Someone returns** | as above | Give them a **new** Keycloak account. A withdrawn account can't be bound again. |
| **Forgotten PIN** | same people who may withdraw them | Registrars → key button → **Reset PIN**. You never choose or learn the new one: they set it on a tablet with signal. |
| **Changed role** (e.g. registrar becomes officer) | system admin | Withdraw the old binding, create a new Keycloak account with the new role, and bind it. One account, one role. |
| **Moved facility or county** | system admin (or the officer, for staff within their county) | A binding's facility can't be changed. Withdraw it, then give them a new Keycloak account in the new county group and bind it at the new facility. Leaving an officer's group and facility disagreeing gets every request refused. |
| **Lost phone** (one-time code) | realm admin | Users → the person → **Credentials** → delete the OTP credential. They set up a new one at the next sign-in. **Confirm who is asking first:** this is the one step that bypasses the second factor. |
| **Locked out** | realm admin | After 5 wrong passwords the account is locked for 60 s, rising to 15 min. Wait, or clear the temporary lock on the user's page. Keycloak never locks permanently, on purpose. |
| **Lost tablet** | district officer | Revoke the device in NCBRS **and** sign out its offline session in Keycloak. See RUNBOOK, "Incident: a tablet is lost or stolen". |

---

## 8. The first system admin

Binding a waiting account needs a system admin who is already bound, and so
does creating a facility. On a new, empty registry (the first production
install, or after the go-live discard), nobody is. So the first one is bound
from the command line, once:

1. Create their Keycloak account with `system-admin` and no county group (§6.1).
2. They sign in to the web site once and set up their one-time code (§6.2).
3. The deployer runs, with the Api's production configuration:

   ```
   NCBRS.Api bootstrap --Bootstrap:Username=<username> \
     --Bootstrap:FacilityName="Ministry of Health headquarters" \
     --Bootstrap:Area=SS0101 --Bootstrap:OfficeCode=MOH
   ```

   This loads the official geography, creates the headquarters facility (or
   uses one already there with that name), and binds them as system admin.
4. They sign out and in again, then bind a **second** system admin from the
   web site.

It is refused while any active system admin exists. From then on everything
in §6 works, starting with creating facilities. The full procedure is in
RUNBOOK, "Deployment: a new registry".

---

## 9. Development fixture accounts

Every dev account has the password `password`. The three that need a code share
the fixture secret `ncbrs-dev-otp-fixture-not-a-secret`. Generate its code with
`web/e2e/totp.ts`, or add the secret to an authenticator app.

| Username | Role | County group | One-time code |
|---|---|---|:-:|
| `nurse.lado` | facility-registrar | none | |
| `dr.kenyi` | facility-registrar | none | |
| `district.officer` | district-officer | `/counties/SS0101` | ✓ |
| `registrar.general` | registrar-general | none | ✓ |
| `system.admin` | system-admin | none | ✓ |
| `service-account-ncbrs-district` | district-officer (District node) | `/counties/SS0101` | n/a |

After changing the realm file, run
`docker compose up -d --force-recreate keycloak`.

---

## 10. Changing who may do what

1. Edit `NcbrsRoles.Permissions`.
2. Run the tests. `RolePermissionsTests.TheWebReadsTheSameTable` fails and
   prints the new `web/src/auth/permissions.json`. Paste that in.
3. A **new role** also needs:
   - the constant in `NcbrsRoles` and a `RegistrarRole` value;
   - the realm role in `keycloak/ncbrs-realm.json`, with `otp-required` as a
     composite unless it is a facility role
     (`KeycloakRealmHardeningTests` fails until it has it);
   - this page.
4. A **new endpoint** must carry a policy, `[AllowAnonymous]`, or a reasoned
   entry in `RolePermissionsTests.AnySignedInCaller`, or the build fails.
