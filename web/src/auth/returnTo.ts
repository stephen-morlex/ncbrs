/**
 * Where to send the user once signed in: the page they originally asked for,
 * which `RequireAuth` put into the OIDC `state` before redirecting.
 *
 * **Only same-site paths are honoured.** The value survives a round trip
 * through the browser and the identity provider, so treating it as a
 * destination unchecked would make the callback an open redirect: a crafted
 * link could send someone to Keycloak, have them sign in genuinely, and land
 * them on an attacker's page still believing they are inside NCBRS. For a
 * system whose users are trained to sign in and then enter identity data,
 * that is the phishing primitive worth closing.
 *
 * Requiring exactly one leading slash rejects absolute URLs
 * (`https://evil.example`) and protocol-relative ones (`//evil.example`),
 * which browsers treat as absolute and which a single `startsWith('/')` check
 * would wave through.
 *
 * Lives apart from the component that calls it so it can be tested directly:
 * every branch here is a refusal, and a refusal that silently stopped
 * refusing would look like nothing at all.
 */
export function returnTo(state: unknown): string {
  const candidate = (state as { returnTo?: unknown } | undefined)?.returnTo

  if (typeof candidate !== 'string') {
    return '/'
  }

  if (!candidate.startsWith('/') || candidate.startsWith('//')) {
    return '/'
  }

  // A backslash after the leading slash is the same trick: some browsers
  // normalise "/\evil.example" to "//evil.example" and follow it off-site.
  if (candidate.startsWith('/\\')) {
    return '/'
  }

  // Landing back on the callback route would render that component again
  // with no code left to redeem.
  if (candidate.startsWith('/auth/callback')) {
    return '/'
  }

  return candidate
}
