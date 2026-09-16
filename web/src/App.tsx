import { Navigate, Route, Routes } from 'react-router'
import { Construction } from 'lucide-react'
import {
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from '@/components/ui/empty'
import { AuthCallback } from '@/auth/AuthCallback'
import { RequireAuth } from '@/auth/RequireAuth'
import { AppLayout } from '@/shell/AppLayout'
import { navigation } from '@/shell/navigation'
import { RecordLookup } from '@/records/RecordLookup'

/**
 * Routing for the shell.
 *
 * Every page beyond the callback sits inside `RequireAuth` and the layout, so
 * there is no arrangement of routes that renders a page without a session.
 * The destinations not yet built are still routed, gated by the same policy
 * as their navigation entry -- so the guards and the 403 screen are exercised
 * against the real policy list rather than a single example.
 */
export default function App() {
  const pending = navigation.flatMap((group) =>
    group.items.filter((item) => item.pending).map((item) => ({ ...item, group: group.label })),
  )

  return (
    <Routes>
      {/* Must match oidcConfig's redirect_uri and the realm's redirectUris.
          Without this route the code exchange still succeeds and the user
          lands on a blank page. */}
      <Route path="/auth/callback" element={<AuthCallback />} />

      <Route
        element={
          <RequireAuth>
            <AppLayout />
          </RequireAuth>
        }
      >
        <Route path="/" element={<Navigate to="/records" replace />} />
        <Route path="/records" element={<RecordLookup />} />

        {pending.map((item) => (
          <Route
            key={item.to}
            path={item.to}
            element={
              <RequireAuth policy={item.policy ?? undefined}>
                <NotBuiltYet title={item.label} />
              </RequireAuth>
            }
          />
        ))}

        <Route path="*" element={<NotFound />} />
      </Route>
    </Routes>
  )
}

function NotBuiltYet({ title }: { title: string }) {
  return (
    <Empty className="border">
      <EmptyHeader>
        <EmptyMedia variant="icon">
          <Construction />
        </EmptyMedia>
        <EmptyTitle>{title}</EmptyTitle>
        <EmptyDescription>
          Routed and gated, but not built yet. The navigation shows it so the shape of the system
          is legible before every part of it exists.
        </EmptyDescription>
      </EmptyHeader>
    </Empty>
  )
}

function NotFound() {
  return (
    <Empty className="border">
      <EmptyHeader>
        <EmptyMedia variant="icon">
          <Construction />
        </EmptyMedia>
        <EmptyTitle>No such page</EmptyTitle>
        <EmptyDescription>Check the address, or use the navigation.</EmptyDescription>
      </EmptyHeader>
    </Empty>
  )
}
