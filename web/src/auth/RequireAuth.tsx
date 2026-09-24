import { type ReactNode, useEffect } from 'react'
import { useLocation } from 'react-router'
import { useTranslation } from 'react-i18next'
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
  const { t } = useTranslation()

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
          <EmptyTitle>{t('auth.couldNotSignIn')}</EmptyTitle>
          <EmptyDescription>{auth.error.message}</EmptyDescription>
        </EmptyHeader>
        <EmptyContent>
          <Button variant="outline" onClick={() => void auth.signinRedirect()}>
            {t('auth.tryAgain')}
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
          <EmptyTitle>{t('auth.signingIn')}</EmptyTitle>
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
  const { t, i18n } = useTranslation()
  const roles = realmRoles(auth.user)
  const list = (values: readonly string[]) => formatRoles(values, i18n.language, t('auth.noRole'))

  return (
    <AuthStatus>
      <EmptyHeader>
        <EmptyMedia variant="icon">
          <ShieldX />
        </EmptyMedia>
        <EmptyTitle>{t('auth.forbiddenTitle')}</EmptyTitle>
        <EmptyDescription>
          {t('auth.needsRoles', { roles: list(NcbrsPolicies[policy]) })}{' '}
          {roles.length > 0 ? t('auth.signedInAs', { roles: list(roles) }) : t('auth.noRoles')}
        </EmptyDescription>
      </EmptyHeader>
      <EmptyContent>{t('auth.askDistrictOfficer')}</EmptyContent>
    </AuthStatus>
  )
}

/**
 * "a, b or c" in the reader's language. A list is not a join with ", " and
 * " or ": the conjunction, its position and the comma rules all differ between
 * languages, which is what Intl.ListFormat exists for.
 */
function formatRoles(roles: readonly string[], language: string, none: string): string {
  if (roles.length === 0) {
    return none
  }

  return new Intl.ListFormat(language, { type: 'disjunction' }).format(roles)
}
