import { describe, expect, it } from 'vitest'
import { realmRolesFromToken } from '@/auth/claims'

/**
 * Builds a JWT-shaped string. Unsigned on purpose: the reader never verifies
 * a signature, because the API does that on every call and a client-side
 * check would prove nothing the server has not already established.
 */
function token(payload: unknown): string {
  const encode = (value: unknown) => {
    // UTF-8 then base64url, which is what Keycloak emits. Passing the string
    // straight to btoa would encode it as Latin-1, and any name outside
    // ASCII would then decode to mojibake or throw -- a difference the
    // ASCII-only cases below cannot show.
    const bytes = new TextEncoder().encode(JSON.stringify(value))
    const binary = Array.from(bytes, (byte) => String.fromCharCode(byte)).join('')

    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
  }

  return `${encode({ alg: 'RS256' })}.${encode(payload)}.signature`
}

describe('realmRolesFromToken', () => {
  it('reads the roles Keycloak nests under realm_access', () => {
    expect(realmRolesFromToken(token({ realm_access: { roles: ['district-officer'] } }))).toEqual([
      'district-officer',
    ])
  })

  it('keeps every role, since a user may hold more than one', () => {
    const roles = ['facility-registrar', 'district-officer']

    expect(realmRolesFromToken(token({ realm_access: { roles } }))).toEqual(roles)
  })

  it('handles a base64url payload containing characters that need translating', () => {
    // Keycloak signs real names; a display name with non-ASCII produces
    // padding and +/ characters that plain atob would mishandle.
    const payload = { realm_access: { roles: ['ministry-admin'] }, name: 'Ayen Deng-Wani ø' }

    expect(realmRolesFromToken(token(payload))).toEqual(['ministry-admin'])
  })

  describe('returns no roles rather than throwing', () => {
    // Failing closed is the point: no roles means the UI offers nothing, and
    // the API refuses the call regardless. A thrown error would take the
    // whole app down over a malformed claim.
    it.each([
      ['undefined', undefined],
      ['an empty string', ''],
      ['not a JWT', 'not-a-jwt'],
      ['too few segments', 'a.b'],
      ['an unparseable payload', 'aGVhZGVy.bm90LWpzb24.sig'],
    ])('%s', (_label, value) => {
      expect(realmRolesFromToken(value)).toEqual([])
    })
  })

  describe('ignores a realm_access that is the wrong shape', () => {
    it.each([
      ['no realm_access at all', {}],
      ['realm_access without roles', { realm_access: {} }],
      ['roles that are not an array', { realm_access: { roles: 'district-officer' } }],
      ['roles that are null', { realm_access: { roles: null } }],
    ])('%s', (_label, payload) => {
      expect(realmRolesFromToken(token(payload))).toEqual([])
    })
  })

  it('drops non-string entries rather than passing them through as roles', () => {
    const payload = { realm_access: { roles: ['district-officer', 42, null, 'ministry-admin'] } }

    expect(realmRolesFromToken(token(payload))).toEqual(['district-officer', 'ministry-admin'])
  })
})
