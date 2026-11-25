/**
 * The realm roles, and which of them satisfy each of the API's policies.
 *
 * A mirror of `NcbrsRoles.Permissions` in `src/NCBRS.Core/Web/NcbrsRoles.cs`,
 * the one table both services build their policies from. It is a second copy, which
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

import permissions from './permissions.json'

export const NcbrsRoles = {
  FacilityRegistrar: 'facility-registrar',
  CommunityHealthWorker: 'community-health-worker',
  DistrictOfficer: 'district-officer',
  /** The Ministry's records role: national approvals, annulment, audit, reporting (decision D2). */
  RegistrarGeneral: 'registrar-general',
  /** The Ministry's systems role: facilities, areas, registrars, devices. Sees no birth record (decision D2). */
  SystemAdmin: 'system-admin',
} as const

export type NcbrsRole = (typeof NcbrsRoles)[keyof typeof NcbrsRoles]

/**
 * Policy name -> the roles that satisfy it. **Read from the server's own
 * table**, `NcbrsRoles.Permissions`, through `permissions.json`, which a server
 * test (`RolePermissionsTests`) holds equal to it, as the OpenAPI drift check
 * holds the contract. So the two cannot disagree; the reasons for each entry
 * are documented beside the server's table, where the decision lives.
 */
export const NcbrsPolicies = {
  ...(permissions as Record<keyof typeof permissions, readonly string[]>),

  /**
   * The whole country, or any county by choice: the registrar-general's. A
   * district officer's dashboard is their county. Decides whether the
   * dashboard offers an area picker at all. A web-only refinement of
   * CanReadReporting; the consumer applies it through its scope.
   */
  CanReadNationalReporting: [NcbrsRoles.RegistrarGeneral] as readonly string[],
}

export type NcbrsPolicy = keyof typeof NcbrsPolicies

export function hasRole(roles: readonly string[], role: NcbrsRole): boolean {
  return roles.includes(role)
}

export function satisfies(roles: readonly string[], policy: NcbrsPolicy): boolean {
  return NcbrsPolicies[policy].some((role) => roles.includes(role))
}

/**
 * Roles permitted to act beyond a single facility.
 *
 * Beyond one facility is not everywhere: a district officer reaches only the
 * facilities of their own county, and only the Ministry's two roles act
 * nationally. The server enforces that (CanActForFacilityAsync); this is a
 * courtesy for hiding affordances, never a check.
 */
export function isCrossFacility(roles: readonly string[]): boolean {
  return [NcbrsRoles.DistrictOfficer, NcbrsRoles.RegistrarGeneral, NcbrsRoles.SystemAdmin].some((role) =>
    roles.includes(role),
  )
}
