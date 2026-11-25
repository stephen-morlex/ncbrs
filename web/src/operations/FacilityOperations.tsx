import { type ReactNode, useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router'
import { useAuth } from 'react-oidc-context'
import { Activity } from 'lucide-react'
import { Card, CardContent } from '@/components/ui/card'
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from '@/components/ui/empty'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import type { components } from '@/api/generated/api'
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'
import { realmRoles } from '@/auth/claims'
import { type NcbrsPolicy, satisfies } from '@/auth/roles'
import { PageHeader } from '@/shell/PageHeader'
import { ageInWords, QueueFailure, QueueSkeleton } from '@/review/queueParts'

type Report = components['schemas']['FacilityOperationsReport']
type Row = components['schemas']['FacilityOperations']
type Backlog = components['schemas']['Backlog']
type Profile = Row['connectivityProfile']

const DayMs = 86_400_000

/**
 * How long a facility may go without reaching the registry before it is worth
 * a look: the device-silence thresholds (2 / 7 / 21 days), because a village
 * post a fortnight offline is working as designed and a hospital terminal two
 * days quiet is not.
 */
const QuietAfterDays: Record<Profile, number> = {
  AlwaysOn: 2,
  Intermittent: 7,
  OfflineFirst: 21,
}

/** A queue item older than this has waited longer than anyone should. */
const StaleAfterDays = 7

/** A family waiting longer than this for a certificate is worth chasing. */
const UncollectedAfterDays = 30

function olderThan(iso: string | null | undefined, days: number, now: number): boolean {
  return iso != null && now - new Date(iso).getTime() > days * DayMs
}

/**
 * How each facility is doing, for the people keeping the pilot running (plan
 * §17 item 35): births refused at sync, numbers still provisional, review
 * queues and their age, devices gone quiet, certificates not yet collected.
 *
 * **What needs a look is marked, and every figure links to the list behind
 * it** where the reader may open that list. The registry counts each figure
 * with the same definition as its list, so the two never disagree.
 *
 * **"Not recorded" is not zero.** Sync batches from before the registry kept
 * their outcomes carry no refusal count, and a facility with only those shows
 * a dash, never a reassuring 0.
 */
export function FacilityOperations() {
  const api = useApiClient()
  const auth = useAuth()
  const roles = realmRoles(auth.user)
  const can = (policy: NcbrsPolicy) => satisfies(roles, policy)

  const [report, setReport] = useState<Report | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(true)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)

    try {
      const { data, error: failure, response } = await api.GET('/api/operations/facilities')
      if (failure || !response.ok) {
        setError(toNcbrsError(failure, response.status))
        return
      }

      setReport(data?.data ?? null)
    } catch (cause) {
      setError(unreachableError(cause))
    } finally {
      setLoading(false)
    }
  }, [api])

  useEffect(() => {
    void load()
  }, [load])

  // Ages are measured to when the registry answered, so the marks are as of
  // the figures they mark, and rendering stays pure.
  const now = report ? new Date(report.generatedAtUtc).getTime() : 0

  return (
    <div className="mx-auto w-full max-w-7xl space-y-6">
      <PageHeader
        title="Facility operations"
        description="What needs attention at each facility: births refused at sync, numbers not yet confirmed, reviews waiting, devices gone quiet and certificates not yet collected."
      />

      {loading && report === null ? <QueueSkeleton /> : null}

      {error ? (
        <QueueFailure error={error} onRetry={() => void load()} fallback="The operations view could not be loaded." />
      ) : null}

      {report && !error ? (
        report.facilities.length === 0 ? (
          <Empty className="border">
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <Activity />
              </EmptyMedia>
              <EmptyTitle>No facilities</EmptyTitle>
              <EmptyDescription>There are no facilities in your area yet.</EmptyDescription>
            </EmptyHeader>
          </Empty>
        ) : (
          <Card>
            <CardContent className="pt-6">
              <p className="text-muted-foreground mb-3 text-sm">
                Births and sync figures cover the last {report.windowDays} days. Queues and certificates are as
                they stand now. <span className="text-destructive font-medium">Marked</span> figures need a look.
              </p>

              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Facility</TableHead>
                    <TableHead>Last sync</TableHead>
                    <TableHead className="text-right">Births</TableHead>
                    <TableHead className="text-right">Refused at sync</TableHead>
                    <TableHead className="text-right">Numbers</TableHead>
                    <TableHead className="text-right">Corrections</TableHead>
                    <TableHead className="text-right">Late</TableHead>
                    <TableHead className="text-right">Duplicates</TableHead>
                    <TableHead className="text-right">Certificates</TableHead>
                    <TableHead className="text-right">Devices</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {report.facilities.map((row) => (
                    <TableRow key={row.facilityId}>
                      <TableCell className="align-top">
                        <div className="font-medium">{row.name}</div>
                        <div className="text-muted-foreground font-mono text-xs">{row.countyCode}</div>
                      </TableCell>

                      <TableCell className="align-top whitespace-nowrap">
                        <Flag on={row.lastSyncAtUtc == null || olderThan(row.lastSyncAtUtc, QuietAfterDays[row.connectivityProfile], now)}>
                          {row.lastSyncAtUtc ? ageInWords(row.lastSyncAtUtc) : 'Never'}
                        </Flag>
                      </TableCell>

                      <TableCell className="text-right align-top tabular-nums">{row.registrationsInWindow}</TableCell>

                      <TableCell className="text-right align-top tabular-nums">
                        {row.refusedRecordsInWindow == null ? (
                          <span className="text-muted-foreground" title="Not recorded for the batches in this period">
                            —
                          </span>
                        ) : (
                          <Flag on={row.refusedRecordsInWindow > 0}>
                            {row.refusedRecordsInWindow} of {row.syncedRecordsInWindow}
                          </Flag>
                        )}
                      </TableCell>

                      <TableCell className="text-right align-top tabular-nums">
                        <Flag on={row.provisionalOpen > 0 || row.unconfirmedNumbers > 0}>
                          {row.provisionalOpen + row.unconfirmedNumbers}
                        </Flag>
                        {row.provisionalOpen + row.unconfirmedNumbers > 0 ? (
                          <span className="text-muted-foreground block text-xs">
                            {row.provisionalOpen} provisional, {row.unconfirmedNumbers} unconfirmed
                          </span>
                        ) : null}
                      </TableCell>

                      <BacklogCell backlog={row.pendingCorrections} now={now} to={can('CanApproveAmendments') ? '/review/amendments' : null} />
                      <BacklogCell backlog={row.pendingLateRegistrations} now={now} to={can('CanApproveLateRegistrations') ? '/review/late-registrations' : null} />
                      <BacklogCell backlog={row.pendingDuplicates} now={now} to={can('CanReviewDuplicates') ? '/review/duplicates' : null} />

                      <TableCell className="text-right align-top tabular-nums">
                        <MaybeLink to={row.certificatesToIssue + row.certificatesToHandOver > 0 ? '/certificates/queue' : null}>
                          <Flag on={olderThan(row.oldestUncollectedRegisteredAtUtc, UncollectedAfterDays, now)}>
                            {row.certificatesToIssue + row.certificatesToHandOver}
                          </Flag>
                        </MaybeLink>
                        {row.certificatesToIssue + row.certificatesToHandOver > 0 ? (
                          <span className="text-muted-foreground block text-xs">
                            {row.certificatesToIssue} to issue, {row.certificatesToHandOver} to hand over
                          </span>
                        ) : null}
                      </TableCell>

                      <TableCell className="text-right align-top tabular-nums">
                        {row.activeDevices}
                        {row.openDeviceAlerts > 0 ? (
                          <MaybeLink to={can('CanEnrolDevices') ? '/devices/alerts' : null}>
                            <span className="text-destructive block text-xs font-medium">
                              {row.openDeviceAlerts === 1 ? '1 alert' : `${row.openDeviceAlerts} alerts`}
                            </span>
                          </MaybeLink>
                        ) : null}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </CardContent>
          </Card>
        )
      ) : null}
    </div>
  )
}

/** Marks a figure that needs a look, in words as well as colour, so it does not rely on colour alone. */
function Flag({ on, children }: { on: boolean; children: ReactNode }) {
  return on ? (
    <span className="text-destructive font-medium">
      {children}
      <span className="sr-only"> (needs attention)</span>
    </span>
  ) : (
    <>{children}</>
  )
}

function MaybeLink({ to, children }: { to: string | null; children: ReactNode }) {
  return to ? (
    <Link to={to} className="underline-offset-4 hover:underline">
      {children}
    </Link>
  ) : (
    <>{children}</>
  )
}

function BacklogCell({ backlog, now, to }: { backlog: Backlog; now: number; to: string | null }) {
  return (
    <TableCell className="text-right align-top tabular-nums">
      <MaybeLink to={backlog.count > 0 ? to : null}>
        <Flag on={olderThan(backlog.oldestAtUtc, StaleAfterDays, now)}>{backlog.count}</Flag>
      </MaybeLink>
      {backlog.oldestAtUtc ? (
        <span className="text-muted-foreground block text-xs whitespace-nowrap">oldest {ageInWords(backlog.oldestAtUtc)}</span>
      ) : null}
    </TableCell>
  )
}
