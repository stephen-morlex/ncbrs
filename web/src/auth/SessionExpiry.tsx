import { useCallback, useEffect, useState } from 'react'
import { useAuth } from 'react-oidc-context'
import { TriangleAlert } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'

/**
 * Warns before the session ends, while there is still time to do something
 * about it.
 *
 * **Why it has to fire on "expiring" rather than "expired".** When the token
 * actually dies, `RequireAuth` sees `isAuthenticated` go false and immediately
 * calls `signinRedirect()` — a full page navigation. Tokens are held in memory
 * only (see `oidc.ts`), so that redirect takes any half-completed registration
 * form with it, and a warning shown at that point would never be read. The
 * `accessTokenExpiring` event fires while the user is *still signed in*, which
 * is the only window in which a warning is actionable.
 *
 * Silent renew normally succeeds and this disappears on its own, so the warning
 * is a non-modal banner rather than a dialog: a registrar part-way through a
 * birth registration must not have a modal thrown in front of the form they are
 * typing into, for something that usually resolves itself. It escalates in
 * wording — not in intrusiveness — once renewal has actually failed, because
 * from then on the redirect really is coming.
 */
export function SessionExpiry() {
  const auth = useAuth()
  const [state, setState] = useState<'none' | 'expiring' | 'failed'>('none')
  const [retrying, setRetrying] = useState(false)

  useEffect(() => {
    const events = auth.events

    // Fired shortly before expiry, while the session is still valid.
    const offExpiring = events.addAccessTokenExpiring(() => {
      setState((current) => (current === 'failed' ? current : 'expiring'))
    })

    // Renewal actually failed, or the token lapsed without one succeeding.
    // From here the sign-in redirect is coming, so say so plainly.
    const offError = events.addSilentRenewError(() => setState('failed'))
    const offExpired = events.addAccessTokenExpired(() => setState('failed'))

    // A renewal landed: the session is healthy again and the warning is stale.
    const offLoaded = events.addUserLoaded(() => {
      setState('none')
      setRetrying(false)
    })

    return () => {
      offExpiring()
      offError()
      offExpired()
      offLoaded()
    }
  }, [auth.events])

  const renew = useCallback(async () => {
    setRetrying(true)
    try {
      await auth.signinSilent()
      // `addUserLoaded` clears the banner on success; if the provider resolves
      // without raising it, clear here so the banner cannot stick.
      setState('none')
    } catch {
      setState('failed')
    } finally {
      setRetrying(false)
    }
  }, [auth])

  if (state === 'none') {
    return null
  }

  const failed = state === 'failed'

  return (
    <Alert variant={failed ? 'destructive' : 'default'} role="status" aria-live="polite">
      <TriangleAlert />
      <AlertTitle>{failed ? 'Your session has ended' : 'Your session is about to expire'}</AlertTitle>
      <AlertDescription className="flex flex-wrap items-center gap-3">
        <span>
          {failed
            ? 'Signing in again will reload this page, and anything typed here and not yet submitted will be lost. Copy any unsaved details before continuing.'
            : 'If it expires you will be signed in again, which reloads this page and discards anything not yet submitted.'}
        </span>
        <Button size="sm" variant={failed ? 'secondary' : 'outline'} onClick={() => void renew()} disabled={retrying}>
          {retrying ? 'Signing in…' : 'Stay signed in'}
        </Button>
      </AlertDescription>
    </Alert>
  )
}
