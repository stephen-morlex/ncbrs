/**
 * The realm roles, and which of them satisfy each of the API's policies.
 *
 * A mirror of `NcbrsRoles` in `src/NCBRS.Core/Web/NcbrsRoles.cs` and the
 * policies registered in `NCBRS.Api/Program.cs`. It is a second copy, which
 * is a cost worth naming: if the server adds a role to a policy and this is
 * not updated, the UI hides a queue the user is in fact allowed to work.
 * That failure is visible and annoying. The reverse -- this being more
 * generous than the server -- shows the user a screen that then 403s, which
 * is the behaviour the guard below is designed around anyway.
 *
 * **The server is the control; this is a courtesy.** Nothing here is a
 * security boundary. Every endpoint enforces its own policy, and a caller who
 * edits this file, or the token in memory, gains exactly nothing: the API
 * re-derives roles from a signature it verifies against Keycloak. What this
 * buys is a district officer not being shown four empty review queues, and a
 * facility registrar not being offered an annulment button that can only
 * fail.
 */

export const NcbrsRoles = {
  FacilityRegistrar: 'facility-registrar',
  CommunityHealthWorker: 'community-health-worker',
  DistrictOfficer: 'district-officer',
  MinistryAdmin: 'ministry-admin',
} as const

export type NcbrsRole = (typeof NcbrsRoles)[keyof typeof NcbrsRoles]

const Oversight: readonly NcbrsRole[] = [NcbrsRoles.DistrictOfficer, NcbrsRoles.MinistryAdmin]

/**
 * Policy name -> the roles that satisfy it, exactly as
 * `AddPolicy(...).RequireRole(...)` has it on the server.
 */
export const NcbrsPolicies = {
  /** Anyone permitted to register a birth. */
  CanRegisterBirths: [
    NcbrsRoles.FacilityRegistrar,
    NcbrsRoles.CommunityHealthWorker,
    ...Oversight,
  ],

  /** Whether a citizen has one legal identity or two. Oversight, not the filer. */
  CanReviewDuplicates: Oversight,

  /**
   * Which person the register describes. Oversight only -- and the server
   * additionally refuses an approval by the submitter, whatever their role,
   * which no UI check can substitute for.
   */
  CanApproveAmendments: Oversight,

  /** Confirms a date of birth nobody contemporaneous can contradict. */
  CanApproveLateRegistrations: Oversight,

  /** Withdraws a legal identity outright. A level above the rest: ministry only. */
  CanAnnulRegistrations: [NcbrsRoles.MinistryAdmin],

  /** Decides which hardware may write to the register at all. */
  CanEnrolDevices: Oversight,

  /**
   * National vital statistics. Not a facility registrar's to read -- their
   * work is a record at a time. Matches the consumer's `ncbrs-reporting`.
   */
  CanReadReporting: Oversight,
} as const satisfies Record<string, readonly NcbrsRole[]>

export type NcbrsPolicy = keyof typeof NcbrsPolicies

export function hasRole(roles: readonly string[], role: NcbrsRole): boolean {
  return roles.includes(role)
}

export function satisfies(roles: readonly string[], policy: NcbrsPolicy): boolean {
  return NcbrsPolicies[policy].some((role) => roles.includes(role))
}

/** Roles permitted to act beyond a single facility. */
export function isCrossFacility(roles: readonly string[]): boolean {
  return Oversight.some((role) => roles.includes(role))
}
