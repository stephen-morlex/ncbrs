import path from 'node:path'

/**
 * The development realm's test accounts (keycloak/ncbrs-realm.json), each
 * bound by the development seed to a registrar at Juba Teaching Hospital.
 *
 * These are committed fixtures for a local, throwaway identity provider — not
 * credentials to anything real. The password can be overridden for a realm
 * that uses a different one.
 */
const password = process.env.E2E_PASSWORD ?? 'password'

/**
 * District officers and both Ministry roles need a one-time code at every
 * sign-in (decision D3). Their dev-realm accounts carry this fixture secret,
 * so the tests type a code exactly as a person would, through the same flow
 * production uses. Overridable, like the password.
 */
const otpSecret = process.env.E2E_OTP_SECRET ?? 'ncbrs-dev-otp-fixture-not-a-secret'

export const Users = {
  /** facility-registrar: files registrations, requests corrections, issues certificates. */
  registrar: { username: 'nurse.lado', password },
  /** district-officer: approves corrections — and must not be the one who asked for them. */
  districtOfficer: { username: 'district.officer', password, otpSecret },
  /** registrar-general: the Ministry's records role, and the only one that may annul. */
  registrarGeneral: { username: 'registrar.general', password, otpSecret },
  /** system-admin: the Ministry's systems role: facilities, areas, registrars, devices; no record. */
  systemAdmin: { username: 'system.admin', password, otpSecret },
} as const satisfies Record<string, { username: string; password: string; otpSecret?: string }>

export type Role = keyof typeof Users

/** Where a role's identity-provider session is kept between the setup and the tests. */
export function storageFor(role: Role): string {
  return path.join(import.meta.dirname, '.auth', `${role}.json`)
}
