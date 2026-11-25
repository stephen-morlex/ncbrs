import { type FormEvent, useCallback, useEffect, useState } from 'react'
import { CircleAlert, LifeBuoy } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from '@/components/ui/empty'
import { Label } from '@/components/ui/label'
import { Spinner } from '@/components/ui/spinner'
import { Textarea } from '@/components/ui/textarea'
import type { components } from '@/api/generated/api'
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'
import { PageHeader } from '@/shell/PageHeader'
import { formatDate } from '@/records/RecordDetail'
import { ageInWords, QueueFailure, QueueSkeleton } from '@/review/queueParts'

type Report = components['schemas']['ProblemReportResponse']
type Diagnostics = components['schemas']['DeviceDiagnostics']

/**
 * Problems reported from tablets, with each tablet's own account of its state
 * (plan §17 item 37), for the district officers who support them.
 *
 * **Newest first**: a report is about now, and the newest is the one a
 * registrar is waiting to hear back on. **Resolving is a person's call**,
 * unlike a silence alert, which only the device reporting again can close: a
 * report says what someone saw, and only someone can say it was dealt with.
 * Resolved reports are kept.
 */
export function ProblemReports() {
  const api = useApiClient()
  const [reports, setReports] = useState<Report[] | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(true)
  const [includeResolved, setIncludeResolved] = useState(false)
  const [resolving, setResolving] = useState<Report | null>(null)

  const load = useCallback(
    async (withResolved: boolean) => {
      setLoading(true)
      setError(null)

      try {
        const { data, error: failure, response } = await api.GET('/api/problem-reports', {
          params: { query: withResolved ? { includeResolved: true } : {} },
        })
        if (failure || !response.ok) {
          setError(toNcbrsError(failure, response.status))
          return
        }

        setReports(data?.data ?? [])
      } catch (cause) {
        setError(unreachableError(cause))
      } finally {
        setLoading(false)
      }
    },
    [api],
  )

  useEffect(() => {
    void load(includeResolved)
  }, [load, includeResolved])

  return (
    <div className="mx-auto w-full max-w-4xl space-y-6">
      <PageHeader
        title="Problem reports"
        description="Problems registrars reported from their tablets, with each tablet's state when they wrote it."
      />

      <div>
        <Button variant="outline" size="sm" onClick={() => setIncludeResolved((value) => !value)}>
          {includeResolved ? 'Hide resolved' : 'Show resolved'}
        </Button>
      </div>

      {loading && reports === null ? <QueueSkeleton /> : null}

      {error ? (
        <QueueFailure error={error} onRetry={() => void load(includeResolved)} fallback="The problem reports could not be loaded." />
      ) : null}

      {reports !== null && !error ? (
        reports.length === 0 ? (
          <Empty className="border">
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <LifeBuoy />
              </EmptyMedia>
              <EmptyTitle>No problems reported</EmptyTitle>
              <EmptyDescription>No tablet in your area has reported a problem that is still open.</EmptyDescription>
            </EmptyHeader>
          </Empty>
        ) : (
          <ul className="space-y-4">
            {reports.map((report) => (
              <li key={report.reportId}>
                <ReportCard report={report} onResolve={() => setResolving(report)} />
              </li>
            ))}
          </ul>
        )
      ) : null}

      {resolving ? (
        <ResolveDialog
          report={resolving}
          onClose={() => setResolving(null)}
          onResolved={(resolved) => {
            setReports((current) =>
              includeResolved
                ? (current?.map((entry) => (entry.reportId === resolved.reportId ? resolved : entry)) ?? null)
                : (current?.filter((entry) => entry.reportId !== resolved.reportId) ?? null),
            )
            setResolving(null)
          }}
        />
      ) : null}
    </div>
  )
}

function ReportCard({ report, onResolve }: { report: Report; onResolve: () => void }) {
  return (
    <Card>
      <CardContent className="space-y-4 pt-6">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div>
            <p className="font-medium">{report.facilityName}</p>
            <p className="text-muted-foreground text-sm">
              {report.reportedByName} · <span className="font-mono">{report.deviceId}</span>
            </p>
            <p className="text-muted-foreground text-xs" title={`Received ${formatDate(report.receivedAtUtc)}`}>
              Written {ageInWords(report.writtenAtUtc)}, received {ageInWords(report.receivedAtUtc)}
            </p>
          </div>
          {report.resolvedAtUtc ? (
            <Badge variant="secondary">Resolved</Badge>
          ) : (
            <Button variant="outline" size="sm" onClick={onResolve}>
              Resolve
            </Button>
          )}
        </div>

        {report.description ? (
          <p className="whitespace-pre-wrap">{report.description}</p>
        ) : (
          <p className="text-muted-foreground text-sm italic">No description written.</p>
        )}

        <DiagnosticsList diagnostics={report.diagnostics} />

        {report.resolvedAtUtc ? (
          <p className="text-muted-foreground text-sm">
            Resolved {ageInWords(report.resolvedAtUtc)} by {report.resolvedByName ?? 'someone'}
            {report.resolutionNote ? `: ${report.resolutionNote}` : '.'}
          </p>
        ) : null}
      </CardContent>
    </Card>
  )
}

/** What the tablet said about itself. Clock and refusals first: they are the usual cause. */
/**
 * The registry always writes every field; the contract only marks them optional
 * because they have defaults on the C# side. Filled in once, here, so the list
 * below reads plain values.
 */
const Blank: Required<Diagnostics> = {
  appVersion: '',
  platform: '',
  language: '',
  signedIn: false,
  staffCount: 0,
  waitingToSync: 0,
  refused: 0,
  uploadInFlight: false,
  drafts: 0,
  numbersLeft: null,
  provisionalIssued: 0,
  lastSyncedAtUtc: null,
  bundleFetchedAtUtc: null,
  clockVerdict: '',
  clockOffsetSeconds: 0,
  failedPinAttempts: 0,
  pinLockouts: 0,
  lastSyncProblems: [],
}

function DiagnosticsList({ diagnostics: sent }: { diagnostics: Diagnostics }) {
  const diagnostics = { ...Blank, ...sent }
  const clockWrong = diagnostics.clockVerdict !== '' && diagnostics.clockVerdict !== 'Trusted'
  const offsetHours = Math.round(diagnostics.clockOffsetSeconds / 360) / 10

  const rows: [string, string, boolean][] = [
    ['App', `${diagnostics.appVersion} on ${diagnostics.platform} (${diagnostics.language})`, false],
    [
      'Clock',
      clockWrong ? `${diagnostics.clockVerdict} by ${Math.abs(offsetHours)} hours` : 'Correct',
      clockWrong,
    ],
    ['Signed in', diagnostics.signedIn ? 'Yes' : 'No — needs signing in again', !diagnostics.signedIn],
    ['Waiting to sync', String(diagnostics.waitingToSync), false],
    ['Refused by the registry', String(diagnostics.refused), diagnostics.refused > 0],
    ['Numbers left', diagnostics.numbersLeft == null ? 'No block yet' : String(diagnostics.numbersLeft), diagnostics.numbersLeft === 0],
    ['Last sync', diagnostics.lastSyncedAtUtc ? ageInWords(diagnostics.lastSyncedAtUtc) : 'Never', !diagnostics.lastSyncedAtUtc],
    ['Staff who can unlock', String(diagnostics.staffCount), diagnostics.staffCount === 0],
    ['Wrong PINs', `${diagnostics.failedPinAttempts} (${diagnostics.pinLockouts} lockouts)`, diagnostics.pinLockouts > 0],
  ]

  return (
    <div className="space-y-2">
      <dl className="grid grid-cols-1 gap-x-6 gap-y-1 text-sm sm:grid-cols-2">
        {rows.map(([label, value, flagged]) => (
          <div key={label} className="flex justify-between gap-4">
            <dt className="text-muted-foreground">{label}</dt>
            <dd className={flagged ? 'text-destructive font-medium' : undefined}>{value}</dd>
          </div>
        ))}
      </dl>
      {diagnostics.lastSyncProblems.length > 0 ? (
        <div className="text-sm">
          <p className="text-muted-foreground">The last sync reported:</p>
          <ul className="list-disc ps-5">
            {diagnostics.lastSyncProblems.map((problem, index) => (
              <li key={index}>{problem}</li>
            ))}
          </ul>
        </div>
      ) : null}
    </div>
  )
}

function ResolveDialog({
  report,
  onClose,
  onResolved,
}: {
  report: Report
  onClose: () => void
  onResolved: (report: Report) => void
}) {
  const api = useApiClient()
  const [note, setNote] = useState('')
  const [error, setError] = useState<NcbrsError | null>(null)
  const [submitting, setSubmitting] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitting(true)
    setError(null)

    try {
      const { data, error: failure, response } = await api.POST('/api/problem-reports/{reportId}/resolve', {
        params: { path: { reportId: report.reportId } },
        body: { data: { note: note.trim() === '' ? null : note.trim() } },
      })

      if (response.ok && data?.data) {
        onResolved(data.data)
        return
      }

      setError(toNcbrsError(failure, response.status))
    } catch (cause) {
      setError(unreachableError(cause))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : onClose())}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Resolve this report</DialogTitle>
          <DialogDescription>
            From {report.reportedByName} at {report.facilityName}. Say what was done, so the next person who
            reads it knows.
          </DialogDescription>
        </DialogHeader>

        <form onSubmit={submit} className="space-y-4">
          {error ? (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertTitle>{error.title}</AlertTitle>
              <AlertDescription>{error.fields.map((item) => item.message).join(' ')}</AlertDescription>
            </Alert>
          ) : null}

          <div className="grid gap-2">
            <Label htmlFor="resolve-note">What was done (optional)</Label>
            <Textarea id="resolve-note" value={note} onChange={(event) => setNote(event.target.value)} maxLength={500} />
          </div>

          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" disabled={submitting}>
              {submitting ? <Spinner /> : null}
              Mark resolved
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
