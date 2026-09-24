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

export const Users = {
  /** facility-registrar: files registrations, requests corrections, issues certificates. */
  registrar: { username: 'nurse.lado', password },
  /** district-officer: approves corrections — and must not be the one who asked for them. */
  districtOfficer: { username: 'district.officer', password },
  /** ministry-admin: the only role that may annul. */
  ministryAdmin: { username: 'ministry.admin', password },
} as const

export type Role = keyof typeof Users

/** Where a role's identity-provider session is kept between the setup and the tests. */
export function storageFor(role: Role): string {
  return path.join(import.meta.dirname, '.auth', `${role}.json`)
}
