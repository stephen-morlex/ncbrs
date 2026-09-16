import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'

/**
 * Placeholder shell for the central management site.
 *
 * Deliberately almost empty. This PR is the scaffold: its job is to prove
 * that Tailwind v4, the `@/` alias and a vendored shadcn component all
 * resolve and render together, and nothing more. The routing, the generated
 * API client and the Keycloak session arrive in the Phase 1 PRs that follow,
 * and each of those is easier to review against a shell that asserts nothing
 * about them yet.
 */
export default function App() {
  return (
    <main className="mx-auto flex min-h-svh max-w-2xl flex-col justify-center gap-6 p-6">
      <Card>
        <CardHeader>
          <CardTitle>National Civil Birth Registration System</CardTitle>
          <CardDescription>
            Central management site — scaffold only. Sign-in, the register and the
            dashboard are not wired up yet.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <Button disabled>Sign in</Button>
        </CardContent>
      </Card>
    </main>
  )
}
