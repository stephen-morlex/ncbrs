import { Route, Routes } from 'react-router'
import { useAuth } from 'react-oidc-context'
import { Construction, LogOut } from 'lucide-react'
import { Avatar, AvatarFallback } from '@/components/ui/avatar'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { EmptyHeader, EmptyMedia, EmptyTitle, EmptyDescription } from '@/components/ui/empty'
import { Item, ItemActions, ItemContent, ItemDescription, ItemMedia, ItemTitle } from '@/components/ui/item'
import { AuthCallback } from '@/auth/AuthCallback'
import { AuthStatus } from '@/auth/AuthStatus'
import { RequireAuth } from '@/auth/RequireAuth'
import { displayName, realmRoles } from '@/auth/claims'

/**
 * Routes so far. The shell -- navigation, header, the review queues -- is the
 * next PR; this exists to prove the session end to end: sign in against
 * Keycloak, read the roles the API will read, and refuse a page the account
 * is not entitled to.
 */
export default function App() {
  return (
    <Routes>
      {/* Must match oidcConfig's redirect_uri and the realm's redirectUris.
          Without this route the code exchange still succeeds and the user
          lands on a blank page. */}
      <Route path="/auth/callback" element={<AuthCallback />} />

      <Route
        path="/"
        element={
          <RequireAuth>
            <SignedIn />
          </RequireAuth>
        }
      />

      {/* Deliberately gated on the one policy only the ministry satisfies, so
          the 403 screen is reachable by signing in as a district officer
          rather than only in theory. */}
      <Route
        path="/annulments"
        element={
          <RequireAuth policy="CanAnnulRegistrations">
            <Placeholder title="Annulments" />
          </RequireAuth>
        }
      />
    </Routes>
  )
}

function SignedIn() {
  const auth = useAuth()
  const roles = realmRoles(auth.user)
  const name = displayName(auth.user) ?? 'Unknown user'

  return (
    <main className="mx-auto flex min-h-svh max-w-2xl flex-col justify-center p-6">
      <Card>
        <CardHeader>
          <CardTitle>National Civil Birth Registration System</CardTitle>
          <CardDescription>
            The session is live. Navigation and the register arrive with the shell.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <Item variant="outline">
            <ItemMedia>
              <Avatar>
                <AvatarFallback>{initials(name)}</AvatarFallback>
              </Avatar>
            </ItemMedia>
            <ItemContent>
              <ItemTitle>{name}</ItemTitle>
              <ItemDescription>
                <span className="flex flex-wrap gap-1.5">
                  {roles.length > 0 ? (
                    roles.map((role) => (
                      <Badge key={role} variant="secondary">
                        {role}
                      </Badge>
                    ))
                  ) : (
                    // Distinct from "not signed in": the account is genuine
                    // and Keycloak issued a token, but no realm role was
                    // assigned, so every endpoint will refuse it. Saying so
                    // here turns a page of 403s into one answerable question.
                    <Badge variant="destructive">no roles assigned</Badge>
                  )}
                </span>
              </ItemDescription>
            </ItemContent>
            <ItemActions>
              {/* signoutRedirect ends the Keycloak session too, so the next
                  visit is a real sign-in. Clearing only local state would
                  leave the SSO cookie standing and sign the user straight
                  back in -- which on a shared facility terminal is the
                  opposite of signing out. */}
              <Button variant="outline" size="sm" onClick={() => void auth.signoutRedirect()}>
                <LogOut />
                Sign out
              </Button>
            </ItemActions>
          </Item>
        </CardContent>
      </Card>
    </main>
  )
}

function Placeholder({ title }: { title: string }) {
  return (
    <AuthStatus>
      <EmptyHeader>
        <EmptyMedia variant="icon">
          <Construction />
        </EmptyMedia>
        <EmptyTitle>{title}</EmptyTitle>
        <EmptyDescription>Not built yet.</EmptyDescription>
      </EmptyHeader>
    </AuthStatus>
  )
}

function initials(name: string): string {
  return name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() ?? '')
    .join('')
}
