import { Navigate } from 'react-router'
import { useAuth } from 'react-oidc-context'
import { TriangleAlert } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from '@/components/ui/empty'
import { Spinner } from '@/components/ui/spinner'
import { AuthStatus } from '@/auth/AuthStatus'
import { returnTo } from '@/auth/returnTo'

/**
 * Where Keycloak sends the browser back to, carrying the authorization code.
 *
 * `AuthProvider` redeems the code on its own as soon as it sees one in the
 * URL, so this renders the wait and then gets out of the way. It still has to
 * exist as a route: without it the router matches nothing and the user lands
 * on a blank page after signing in successfully -- the exchange having
 * worked, with nothing to show for it.
 *
 * Navigating with `replace` rather than a bare `history.replaceState` is what
 * puts the router back in step. `replaceState` changes the address bar
 * without telling React Router, so the app keeps rendering the route it
 * thinks it is on; and it drops the spent code from the history entry, so a
 * reload cannot replay it.
 */

export function AuthCallback() {
  const auth = useAuth()

  if (auth.error) {
    return (
      <AuthStatus>
        <EmptyHeader>
          <EmptyMedia variant="icon">
            <TriangleAlert />
          </EmptyMedia>
          <EmptyTitle>Sign-in did not complete</EmptyTitle>
          <EmptyDescription>{auth.error.message}</EmptyDescription>
        </EmptyHeader>
        <EmptyContent>
          <Button variant="outline" onClick={() => void auth.signinRedirect()}>
            Try again
          </Button>
        </EmptyContent>
      </AuthStatus>
    )
  }

  if (auth.isAuthenticated) {
    return <Navigate to={returnTo(auth.user?.state)} replace />
  }

  return (
    <AuthStatus>
      <EmptyHeader>
        <EmptyMedia variant="icon">
          <Spinner />
        </EmptyMedia>
        <EmptyTitle>Completing sign-in…</EmptyTitle>
      </EmptyHeader>
    </AuthStatus>
  )
}
