# Pilot readiness review

*2026-10-01.* The Phase 1 gate (plan §10) is that **a genuinely low-connectivity
village post has registered, synced and had certificates verified end to end.**
This review walks that journey as a pilot district would live it, where weeks
offline, dead batteries and wrong clocks are normal, and checks the code at each
step. Findings are ordered by what they block.

## 1. Blocks the pilot: nothing can onboard a facility or a registrar

Every signed-in request resolves the caller to a `Registrar` row by their
Keycloak subject. Outside Development, **there is no way to create that row, or
a facility.** `RegistrarsController` and `FacilitiesController` are read-only;
only the Development seeder creates either.

So a pilot district's first registrar signs in and is refused everywhere
("Account not provisioned"), and so is the tablet's account check at handover.
The web plan's role table already gives "facilities and BRN blocks" to
`ministry-admin`; the write paths were never built.

What onboarding needs:

- **A facility:** name, tier, connectivity profile, its place in the
  administrative tree (which decides its county), and a **BRN range that overlaps
  no other facility's**. Ranges are what decision #2 rests on, so allocating one
  belongs to the registry, never typed by hand.
- **A registrar:** a Keycloak account bound to a facility and a role. The API
  stays a pure resource server (no credentials stored), so the account itself is
  created in Keycloak; NCBRS binds it.
- **Withdrawing a registrar who leaves.** There is no active flag on
  `Registrar`. A registrar who left keeps a PIN that unlocks every tablet at the
  facility until the row is gone, and the trail must keep the name, so the row
  cannot simply be deleted.

Decisions needed: who may onboard a registrar (a district officer for their own
county, or the Ministry only), and how an account is identified when binding it.

## 2. Security: the tablet trusts its own clock

Every time the tablet uses is `DateTime.UtcNow`, the wall clock, which anyone
holding the tablet can change in Android's settings, and which a cheap tablet
can lose when its battery runs flat.

- **The PIN lockout can be skipped.** Five wrong PINs lock the tablet for five
  minutes, measured on the wall clock. Setting the date forward five minutes
  reopens the window. The PIN is the only thing between a stolen tablet and the
  births queued on it.
- **An expired list of withdrawn certificates can be made current.** Staleness
  is measured on the same clock, so setting it back makes a week-old list read
  as fresh, and a certificate withdrawn since then checks as genuine instead of
  "cannot be checked here".
- **A reset clock stops registration.** A tablet whose clock reset to its
  factory date cannot pick today as a date of birth (the picker's maximum is
  "today"), and every capture time precedes the birth. The registrar sees only
  a refusal, not the cause.

None of this needs a decision. The fix is a clock the tablet can defend:

- a high-water mark of the latest time it has trusted (its own readings, and
  the registry's time on every answer), so a clock behind it is **detected,
  shown, and never used**;
- lockouts measured on Android's monotonic elapsed time, which the date setting
  cannot touch, and which escalate when lockouts repeat.

## 3. Needs a decision for the pilot's design

- **How a family at a village post gets the certificate.** A certificate needs
  signal, because only the registry signs one. At a post that is offline for
  weeks, the family leaves with the slip. Where and when the certificate is
  printed and handed over is an operational design for the pilot: on the next
  connected visit, at the district office, or collected.
- **How long a post can check certificates offline.** A list of withdrawn
  certificates is valid for 7 days (`CertificateRevocation:ValidFor`). After
  that, the tablet honestly answers "cannot be checked here" to everything. A
  longer validity means a withdrawal takes longer to reach a post. The Phase 1
  gate's "certificates verified" therefore happens at a connected point unless
  this changes.
- **A post offline for more than 60 days must sign in again** before it can
  sync (the offline token's idle limit). Registration continues offline, and the
  sealed USB transfer still works without a sign-in.

## 4. Needs writing down: onboarding procedures

`RUNBOOK.md` covers incidents and deployment settings well, but none of what a
pilot does on its first day:

1. Create the facility (once §1 exists) and confirm its county.
2. Create the registrar's Keycloak account, with its role and county group.
3. Bind the account to the facility (once §1 exists).
4. The registrar sets a PIN.
5. A district officer hands the tablet over at the post's facility.
6. Pair and test the printer.
7. Withdraw a registrar who leaves: the binding, the Keycloak account, and the
   tablets' next sync.

## Already sound

Checked on the walk and found holding:
- registration fully offline;
- BRN blocks with the provisional fallback;
- atomic encrypted saves;
- refused births held for correction;
- the District node's store-and-forward;
- the sealed USB path for posts with no signal at all;
- staff PINs refreshed at every sync;
- the dashboards' honesty rules.
