import { Suspense, lazy } from 'react'
import { Navigate, Route, Routes } from 'react-router'
import { Construction } from 'lucide-react'
import {
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from '@/components/ui/empty'
import { Spinner } from '@/components/ui/spinner'
import { AuthCallback } from '@/auth/AuthCallback'
import { RequireAuth } from '@/auth/RequireAuth'
import { navigation } from '@/shell/navigation'

/**
 * Pages are fetched when they are opened, not when the app starts.
 *
 * A village clinic on a slow link should not download the audit trail to type
 * a registration number into a search box, and the cost of not splitting
 * grows with every screen Phases 3 to 6 add.
 *
 * `AuthCallback` and `RequireAuth` are deliberately **not** lazy. They run
 * before any route renders, so deferring them would put a chunk fetch in
 * front of the app even deciding whether the user is signed in — a round trip
 * added to the one path every single visit takes.
 */
const AppLayout = lazy(() =>
  import('@/shell/AppLayout').then((module) => ({ default: module.AppLayout })),
)

const RecordLookup = lazy(() =>
  import('@/records/RecordLookup').then((module) => ({ default: module.RecordLookup })),
)

const RecordSearch = lazy(() =>
  import('@/records/RecordSearch').then((module) => ({ default: module.RecordSearch })),
)

const RegisterBirth = lazy(() =>
  import('@/records/RegisterBirth').then((module) => ({ default: module.RegisterBirth })),
)

const RequestCorrection = lazy(() =>
  import('@/records/RequestCorrection').then((module) => ({ default: module.RequestCorrection })),
)

const CertificateManage = lazy(() =>
  import('@/records/CertificateManage').then((module) => ({ default: module.CertificateManage })),
)

const Facilities = lazy(() =>
  import('@/facilities/Facilities').then((module) => ({ default: module.Facilities })),
)

const AuditTrail = lazy(() =>
  import('@/audit/AuditTrail').then((module) => ({ default: module.AuditTrail })),
)

const AmendmentsReview = lazy(() =>
  import('@/review/AmendmentsReview').then((module) => ({ default: module.AmendmentsReview })),
)

const LateRegistrationQueue = lazy(() =>
  import('@/review/LateRegistrationQueue').then((module) => ({
    default: module.LateRegistrationQueue,
  })),
)

const DuplicateReview = lazy(() =>
  import('@/review/DuplicateReview').then((module) => ({ default: module.DuplicateReview })),
)

const AnnulRegistration = lazy(() =>
  import('@/review/AnnulRegistration').then((module) => ({ default: module.AnnulRegistration })),
)

const RegistrarDirectory = lazy(() =>
  import('@/registrars/RegistrarDirectory').then((module) => ({
    default: module.RegistrarDirectory,
  })),
)

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
            {/* One boundary around the layout covers the pages inside it: on
                a cold visit both chunks are in flight together, and a nested
                boundary would flash a second spinner inside a shell that had
                only just appeared. */}
            <Suspense fallback={<LoadingPage />}>
              <AppLayout />
            </Suspense>
          </RequireAuth>
        }
      >
        <Route path="/" element={<Navigate to="/records" replace />} />
        <Route path="/records" element={<RecordLookup />} />
        <Route path="/records/search" element={<RecordSearch />} />

        {/* A correction is an act on a record, not a destination -- reached
            from the record itself, and gated by the same policy as filing
            one. */}
        <Route
          path="/records/correct"
          element={
            <RequireAuth policy="CanRegisterBirths">
              <RequestCorrection />
            </RequireAuth>
          }
        />

        {/* A certificate is an act on a record, reached from it, and gated by
            the same policy as the issue endpoint. */}
        <Route
          path="/records/certificate"
          element={
            <RequireAuth policy="CanRegisterBirths">
              <CertificateManage />
            </RequireAuth>
          }
        />

        {/* Gated by the same policy its navigation entry names, so the guard
            and the nav cannot disagree about who may file a registration. */}
        <Route
          path="/records/new"
          element={
            <RequireAuth policy="CanRegisterBirths">
              <RegisterBirth />
            </RequireAuth>
          }
        />
        <Route path="/facilities" element={<Facilities />} />

        {/* Gated by the same policy its navigation entry names — listing who
            is provisioned is an oversight act. */}
        <Route
          path="/registrars"
          element={
            <RequireAuth policy="CanEnrolDevices">
              <RegistrarDirectory />
            </RequireAuth>
          }
        />

        {/* Gated by the same policy its navigation entry names, so the guard
            and the nav cannot disagree about who may review corrections. */}
        <Route
          path="/review/amendments"
          element={
            <RequireAuth policy="CanApproveAmendments">
              <AmendmentsReview />
            </RequireAuth>
          }
        />

        {/* Gated by the same policy its navigation entry names. */}
        <Route
          path="/review/late-registrations"
          element={
            <RequireAuth policy="CanApproveLateRegistrations">
              <LateRegistrationQueue />
            </RequireAuth>
          }
        />

        {/* Gated by the same policy its navigation entry names. */}
        <Route
          path="/review/duplicates"
          element={
            <RequireAuth policy="CanReviewDuplicates">
              <DuplicateReview />
            </RequireAuth>
          }
        />

        {/* Gated by the same policy its navigation entry names. Ministry-level:
            annulment withdraws a legal identity, not just how it reads. */}
        <Route
          path="/review/annulments"
          element={
            <RequireAuth policy="CanAnnulRegistrations">
              <AnnulRegistration />
            </RequireAuth>
          }
        />

        <Route
          path="/audit"
          element={
            <RequireAuth policy="CanReadAuditTrail">
              <AuditTrail />
            </RequireAuth>
          }
        />

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

/**
 * Shown while a page's chunk is fetched.
 *
 * Matches the sign-in wait on purpose. To the person watching, "fetching the
 * page" and "checking who you are" are the same event — a pause before the
 * thing they asked for — and two different-looking pauses in a row read as
 * something going wrong.
 */
function LoadingPage() {
  return (
    <main className="flex min-h-svh items-center justify-center p-6">
      <Empty className="max-w-md">
        <EmptyHeader>
          <EmptyMedia variant="icon">
            <Spinner />
          </EmptyMedia>
          <EmptyTitle>Loading…</EmptyTitle>
        </EmptyHeader>
      </Empty>
    </main>
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
