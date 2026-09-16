import type { AuthProviderProps } from 'react-oidc-context'
import { InMemoryWebStorage, WebStorageStateStore } from 'oidc-client-ts'

/**
 * The OIDC configuration for `ncbrs-web`.
 *
 * Authorization code + PKCE against Keycloak. The realm has the client as
 * public, `standardFlow` on and `directAccessGrants` **off**, so the password
 * grant is not merely unused here -- it is refused at the identity provider,
 * which is the only place refusing it means anything.
 */

/**
 * Tokens live in memory and nowhere else.
 *
 * `oidc-client-ts` defaults to `sessionStorage`, so this is an override and
 * not a restatement of the default -- deleting it would silently start
 * persisting tokens. Anything in web storage is readable by any script on the
 * origin, which makes one XSS bug the difference between an attacker acting
 * as a registrar for one page view and exfiltrating a credential to use
 * later, elsewhere, at scale. For a register of legal identities that is the
 * distinction worth paying for.
 *
 * The cost is that a page reload has no token. It does *not* sign the user
 * out: Keycloak still holds their SSO session cookie, so the redirect returns
 * immediately with a fresh code and no prompt. What is lost is in-page state,
 * which is a draft-persistence problem and should be solved as one rather
 * than by weakening this.
 *
 * Note this is *not* the strongest available pattern. The current
 * browser-apps BCP recommends a backend-for-frontend, where tokens never
 * reach JavaScript at all. That needs a server component this architecture
 * does not have; moving to it later changes this file and the API client,
 * not every call site.
 */
const inMemoryStore = new WebStorageStateStore({ store: new InMemoryWebStorage() })

export interface KeycloakSettings {
  authority: string
  clientId: string
}

export function readKeycloakSettings(): KeycloakSettings {
  const authority = import.meta.env.VITE_KEYCLOAK_AUTHORITY
  const clientId = import.meta.env.VITE_KEYCLOAK_CLIENT_ID

  // Refused rather than defaulted. A misconfigured authority that quietly
  // fell back to a built-in value would point a production site at whatever
  // that value was, and the failure would look like a login problem rather
  // than a configuration one.
  if (!authority || !clientId) {
    throw new Error(
      'VITE_KEYCLOAK_AUTHORITY and VITE_KEYCLOAK_CLIENT_ID must both be set. ' +
        'Copy web/.env.example to web/.env.local.',
    )
  }

  return { authority, clientId }
}

export function oidcConfig(settings: KeycloakSettings): AuthProviderProps {
  return {
    authority: settings.authority,
    client_id: settings.clientId,

    // Must match the realm's redirectUris exactly. The realm allows
    // http://localhost:5173/* and http://127.0.0.1:5173/* -- and those are
    // different origins to a browser, so whichever the user typed is the one
    // that has to come back.
    redirect_uri: `${window.location.origin}/auth/callback`,
    post_logout_redirect_uri: window.location.origin,

    response_type: 'code',

    // `openid profile` for who they are; `roles` so Keycloak includes
    // realm_access on the access token, which is what authorization is read
    // from. Without `roles` the token validates and the user appears to have
    // no permissions at all -- which looks like a broken account rather than
    // a missing scope.
    scope: 'openid profile roles',

    // The tokens, in memory only. This is the one that matters.
    userStore: inMemoryStore,

    // The transient handshake state -- the PKCE code_verifier and the CSRF
    // `state` -- in sessionStorage, which is also the library's default.
    //
    // It **cannot** be in memory, and the reason is structural rather than a
    // preference: the verifier is created before the redirect to Keycloak and
    // needed after the redirect back, and a redirect is a full page unload.
    // In-memory state does not survive it, so every sign-in fails with "No
    // matching state found in storage" -- not intermittently, always.
    //
    // The exposure is much smaller than the tokens'. A verifier is single-use,
    // lives for the seconds of one round trip, and is worthless on its own:
    // it authorizes nothing and identifies nobody. Script that could read it
    // could equally start its own flow. The long-lived credential, which is
    // what an attacker actually wants, never touches storage.
    stateStore: new WebStorageStateStore({ store: window.sessionStorage }),

    // Renew before expiry using the refresh token. No iframe: `prompt=none`
    // in a hidden iframe is a third-party-cookie flow, which browsers now
    // block by default, so it fails in exactly the deployments that matter.
    automaticSilentRenew: true,
    monitorSession: false,

    // Strips ?code= and ?state= once the exchange is done, so a reload does
    // not replay a spent code and the URL is not left carrying it.
    onSigninCallback: () => {
      window.history.replaceState({}, document.title, window.location.pathname)
    },
  }
}
