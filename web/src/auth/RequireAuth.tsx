import { type ReactNode, useEffect } from 'react'
import { useLocation } from 'react-router'
import { useAuth } from 'react-oidc-context'
import { ShieldX, TriangleAlert } from 'lucide-react'
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
import { realmRoles } from '@/auth/claims'
import { type NcbrsPolicy, NcbrsPolicies, satisfies } from '@/auth/roles'

/**
 * Gates a subtree on being signed in, and optionally on a policy.
 *
 * **The server is the control; this is a courtesy.** Every endpoint enforces
 * its own policy against a token it verifies, so nothing here is a security
 * boundary and editing it grants no access. What it buys is a facility
 * registrar not being shown four review queues that would each answer 403,
 * and a clear explanation when they reach one anyway.
 */
export function RequireAuth({
  children,
  policy,
}: {
  children: ReactNode
  policy?: NcbrsPolicy
}) {
  const auth = useAuth()
  const location = useLocation()

  // Sign-in is a redirect, so it belongs in an effect rather than in render.
  // The guards matter: `isLoading` covers the code exchange and the silent
  // renew, and `activeNavigator` covers a redirect already in flight --
  // without them this fires repeatedly and bounces the user off to Keycloak
  // in a loop.
  useEffect(() => {
    if (!auth.isAuthenticated && !auth.isLoading && !auth.activeNavigator && !auth.error) {
      // Carry where the user was trying to go through the round trip to
      // Keycloak, which comes back on `user.state`. Without it every sign-in
      // lands on the home page, and with tokens in memory that happens on
      // every reload and every deep link -- so a link to a specific record,
      // which is exactly what one registrar sends another, would never open
      // that record.
      void auth.signinRedirect({
        state: { returnTo: `${location.pathname}${location.search}` },
      })
    }
  }, [auth, location])

  if (auth.error) {
    return (
      <AuthStatus>
        <EmptyHeader>
          <EmptyMedia variant="icon">
            <TriangleAlert />
          </EmptyMedia>
          <EmptyTitle>Could not sign you in</EmptyTitle>
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

  // Covers the redirect out, the code exchange on the way back, and the
  // moment after a reload when there is no token in memory yet. All three
  // look the same to the user and none of them is an error.
  if (auth.isLoading || !auth.isAuthenticated) {
    return (
      <AuthStatus>
        <EmptyHeader>
          <EmptyMedia variant="icon">
            <Spinner />
          </EmptyMedia>
          <EmptyTitle>Signing you in…</EmptyTitle>
        </EmptyHeader>
      </AuthStatus>
    )
  }

  if (policy && !satisfies(realmRoles(auth.user), policy)) {
    return <Forbidden policy={policy} />
  }

  return <>{children}</>
}

/**
 * Shown when the user is signed in but the page is not theirs.
 *
 * It names the roles that would grant access rather than saying "denied",
 * because the reader is a colleague who needs to know who to ask -- not an
 * intruder being kept guessing. Nothing here is secret: the policy names and
 * their roles are in the realm and the API's source.
 */
function Forbidden({ policy }: { policy: NcbrsPolicy }) {
  const auth = useAuth()
  const roles = realmRoles(auth.user)

  return (
    <AuthStatus>
      <EmptyHeader>
        <EmptyMedia variant="icon">
          <ShieldX />
        </EmptyMedia>
        <EmptyTitle>This page is not available to your account</EmptyTitle>
        <EmptyDescription>
          It needs {formatRoles(NcbrsPolicies[policy])}.{' '}
          {roles.length > 0
            ? `You are signed in as ${formatRoles(roles)}.`
            : 'Your account has no roles assigned.'}
        </EmptyDescription>
      </EmptyHeader>
      <EmptyContent>
        If this is wrong, a district officer can correct your roles.
      </EmptyContent>
    </AuthStatus>
  )
}

function formatRoles(roles: readonly string[]): string {
  if (roles.length === 0) {
    return 'no role'
  }

  if (roles.length === 1) {
    return roles[0]
  }

  return `${roles.slice(0, -1).join(', ')} or ${roles[roles.length - 1]}`
}
