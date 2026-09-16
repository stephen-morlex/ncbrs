import type { User } from 'oidc-client-ts'

/**
 * Reads Keycloak's realm roles off the access token.
 *
 * Keycloak nests them as `{"realm_access": {"roles": [...]}}`, which is the
 * same shape `KeycloakRealmRoles` flattens on the server. Reading it in two
 * places is unavoidable -- one is C# and one is TypeScript -- but they must
 * agree on where the roles live, so this file and that one are a pair.
 *
 * **From the access token, not the ID token.** The ID token says who signed
 * in; the access token is what the API is handed and what it derives
 * authorization from. Reading roles from the ID token would let the UI and
 * the server disagree about what the caller may do, and the UI would be the
 * one that is wrong.
 */

const RealmAccessClaim = 'realm_access'

interface RealmAccess {
  roles?: unknown
}

/**
 * Decodes a JWT payload without verifying it.
 *
 * Unverified is correct here and not a shortcut. This token was just received
 * over TLS from the authorization endpoint, and nothing security-relevant is
 * decided from it: the API verifies the signature itself on every call. A
 * client-side signature check would need the realm's public keys and would
 * still prove nothing the server has not already established -- it would only
 * make a forged token fail slightly earlier, in the one place where failing
 * late costs nothing.
 */
function decodePayload(token: string): Record<string, unknown> | null {
  const segments = token.split('.')

  if (segments.length !== 3) {
    return null
  }

  try {
    const base64 = segments[1].replace(/-/g, '+').replace(/_/g, '/')
    const json = decodeURIComponent(
      atob(base64)
        .split('')
        .map((char) => `%${`00${char.charCodeAt(0).toString(16)}`.slice(-2)}`)
        .join(''),
    )

    const payload: unknown = JSON.parse(json)

    return typeof payload === 'object' && payload !== null
      ? (payload as Record<string, unknown>)
      : null
  } catch {
    // A malformed token means no roles, never a crash: the UI then shows the
    // caller nothing, and the API refuses them anyway. Failing closed.
    return null
  }
}

export function realmRolesFromToken(accessToken: string | undefined): string[] {
  if (!accessToken) {
    return []
  }

  const payload = decodePayload(accessToken)
  const realmAccess = payload?.[RealmAccessClaim] as RealmAccess | undefined

  if (!Array.isArray(realmAccess?.roles)) {
    return []
  }

  return realmAccess.roles.filter((role): role is string => typeof role === 'string')
}

export function realmRoles(user: User | null | undefined): string[] {
  return realmRolesFromToken(user?.access_token)
}

/**
 * What to call the signed-in user. `preferred_username` is the Keycloak
 * account name and is always present; `name` is nicer but depends on the
 * profile being filled in, which for a registrar provisioned in bulk it may
 * not be.
 */
export function displayName(user: User | null | undefined): string | null {
  const profile = user?.profile

  if (!profile) {
    return null
  }

  return profile.name ?? profile.preferred_username ?? profile.sub ?? null
}
