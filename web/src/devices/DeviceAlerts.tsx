import { useCallback, useEffect, useState } from 'react'
import { BellOff, CircleAlert, TriangleAlert } from 'lucide-react'
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
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from '@/components/ui/empty'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Spinner } from '@/components/ui/spinner'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { Textarea } from '@/components/ui/textarea'
import type { components } from '@/api/generated/api'
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'
import { PageHeader } from '@/shell/PageHeader'
import { formatDate } from '@/records/RecordDetail'

type DeviceAlert = components['schemas']['DeviceAlertResponse']
type Facility = components['schemas']['FacilityResponse']

/**
 * The district's queue of devices that have gone quiet. A silent device is
 * indistinguishable from a district with no births, and only one of those
 * needs someone to drive out — so this queue exists to tell them apart.
 *
 * **Never-reported is a different kind from silent**, because the remedy
 * differs: a device that used to sync and stopped is a link or a battery; one
 * that never has is a deployment that failed at handover. They are labelled
 * apart, not merged.
 *
 * **Acknowledging is not resolving.** It records that someone is dealing with
 * an alert — "I am driving out Thursday" — but only the device reporting again
 * closes it. If acknowledging closed it, a district could empty its queue
 * without a single device coming back, which is the exact gap this surfaces.
 */
export function DeviceAlerts() {
  const api = useApiClient()

  const [alerts, setAlerts] = useState<DeviceAlert[] | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(true)
  const [includeResolved, setIncludeResolved] = useState(false)

  const [facilities, setFacilities] = useState<Facility[]>([])
  const [acknowledging, setAcknowledging] = useState<DeviceAlert | null>(null)

  const load = useCallback(
    async (resolved: boolean) => {
      setLoading(true)
      setError(null)

      try {
        const { data, error: failure, response } = await api.GET('/api/devices/alerts', {
          params: { query: { includeResolved: resolved } },
        })

        if (failure || !response.ok) {
          setError(toNcbrsError(failure, response.status))
          return
        }

        setAlerts(data?.data ?? [])
      } catch (cause) {
        setError(unreachableError(cause))
      } finally {
        setLoading(false)
      }
    },
    [api],
  )

  useEffect(() => {
    let cancelled = false

    void (async () => {
      try {
        const { data, response } = await api.GET('/api/facilities', {
          params: { query: { limit: 200 } },
        })

        if (!cancelled && response.ok) {
          setFacilities(data?.data?.items ?? [])
        }
      } catch {
        // Names are a convenience; the queue works without them.
      }
    })()

    return () => {
      cancelled = true
    }
  }, [api])

  useEffect(() => {
    void load(includeResolved)
  }, [load, includeResolved])

  const facilityName = useCallback(
    (id: string) => facilities.find((facility) => facility.facilityId === id)?.name ?? id,
    [facilities],
  )

  const applyUpdated = useCallback((updated: DeviceAlert) => {
    setAlerts((current) =>
      current?.map((alert) => (alert.deviceAlertId === updated.deviceAlertId ? updated : alert)) ?? null,
    )
  }, [])

  return (
    <div className="mx-auto w-full max-w-5xl space-y-6">
      <PageHeader
        title="Device alerts"
        description="Devices that have gone quiet, so a silent post is not mistaken for one with no births."
      />

      <div className="flex justify-end">
        <Button variant="outline" size="sm" onClick={() => setIncludeResolved((value) => !value)}>
          {includeResolved ? 'Hide resolved' : 'Show resolved'}
        </Button>
      </div>

      {loading && alerts === null ? <LoadingList /> : null}

      {error ? <Failure error={error} onRetry={() => void load(includeResolved)} /> : null}

      {alerts !== null && !error ? (
        alerts.length === 0 ? (
          <Empty className="border">
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <BellOff />
              </EmptyMedia>
              <EmptyTitle>No open alerts</EmptyTitle>
              <EmptyDescription>
                Every enrolled device in your district has reported within its expected window. Use
                “Show resolved” to see ones that came back.
              </EmptyDescription>
            </EmptyHeader>
          </Empty>
        ) : (
          <Card>
            <CardContent className="pt-6">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Device</TableHead>
                    <TableHead>Kind</TableHead>
                    <TableHead>Silent for</TableHead>
                    <TableHead>Status</TableHead>
                    <TableHead className="text-right">Action</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {alerts.map((alert) => (
                    <TableRow key={alert.deviceAlertId}>
                      <TableCell className="align-top">
                        <div className="font-mono text-sm">{alert.deviceId}</div>
                        <div className="text-muted-foreground text-xs">{facilityName(alert.facilityId)}</div>
                      </TableCell>
                      <TableCell className="align-top">
                        <KindBadge alert={alert} />
                      </TableCell>
                      <TableCell className="align-top whitespace-nowrap">
                        {alert.daysSilentWhenRaised} days
                        <span className="text-muted-foreground block text-xs">
                          threshold {alert.thresholdDays} days
                        </span>
                      </TableCell>
                      <TableCell className="align-top">
                        <StatusCell alert={alert} />
                      </TableCell>
                      <TableCell className="text-right align-top">
                        {alert.status === 'Open' ? (
                          <Button variant="outline" size="sm" onClick={() => setAcknowledging(alert)}>
                            Acknowledge
                          </Button>
                        ) : (
                          <span className="text-muted-foreground text-xs">
                            {alert.status === 'Resolved' ? 'device reported' : 'in hand'}
                          </span>
                        )}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </CardContent>
          </Card>
        )
      ) : null}

      {acknowledging ? (
        <AcknowledgeDialog
          alert={acknowledging}
          onClose={() => setAcknowledging(null)}
          onDone={(updated) => {
            applyUpdated(updated)
            setAcknowledging(null)
          }}
        />
      ) : null}
    </div>
  )
}

/**
 * The two kinds are kept visibly apart: sending an officer to diagnose a
 * network fault that was never the problem is the cost of merging them.
 */
function KindBadge({ alert }: { alert: DeviceAlert }) {
  if (alert.kind === 'NeverReported') {
    return (
      <Badge variant="destructive" title="Enrolled but never heard from — a handover that failed.">
        Never reported
      </Badge>
    )
  }
  return (
    <Badge variant="outline" title="Reported before, then stopped — a link or a battery.">
      Went silent
    </Badge>
  )
}

function StatusCell({ alert }: { alert: DeviceAlert }) {
  if (alert.status === 'Resolved') {
    return (
      <span className="text-muted-foreground text-sm">
        Resolved {formatDate(alert.resolvedAtUtc ?? undefined)}
      </span>
    )
  }

  if (alert.status === 'Acknowledged') {
    return (
      <span className="text-sm">
        Acknowledged
        <span className="text-muted-foreground block text-xs" title={alert.acknowledgementNote ?? undefined}>
          by {alert.acknowledgedByRegistrarName ?? 'a registrar'}
          {alert.acknowledgedAtUtc ? `, ${formatDate(alert.acknowledgedAtUtc)}` : ''}
        </span>
      </span>
    )
  }

  return <Badge variant="secondary">Open</Badge>
}

type Phase = { kind: 'confirm' } | { kind: 'submitting' } | { kind: 'error'; error: NcbrsError }

function AcknowledgeDialog({
  alert,
  onClose,
  onDone,
}: {
  alert: DeviceAlert
  onClose: () => void
  onDone: (updated: DeviceAlert) => void
}) {
  const api = useApiClient()

  const [note, setNote] = useState('')
  const [phase, setPhase] = useState<Phase>({ kind: 'confirm' })

  const busy = phase.kind === 'submitting'

  const submit = useCallback(async () => {
    setPhase({ kind: 'submitting' })

    try {
      const { data, error: failure, response } = await api.POST(
        '/api/devices/alerts/{deviceAlertId}/acknowledge',
        {
          params: { path: { deviceAlertId: alert.deviceAlertId } },
          body: { data: { note: note.trim() || null } },
        },
      )

      if (response.ok && data?.data) {
        onDone(data.data)
        return
      }

      setPhase({ kind: 'error', error: toNcbrsError(failure, response.status) })
    } catch (cause) {
      setPhase({ kind: 'error', error: unreachableError(cause) })
    }
  }, [api, alert.deviceAlertId, note, onDone])

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : onClose())}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Acknowledge this alert</DialogTitle>
          <DialogDescription className="font-mono">{alert.deviceId}</DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <p className="text-muted-foreground text-sm">
            This records that you are dealing with it — it does <strong>not</strong> resolve the
            alert. Only the device reporting again does that, so the queue keeps showing it until it
            comes back.
          </p>

          {phase.kind === 'error' ? (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertTitle>{phase.error.title}</AlertTitle>
              <AlertDescription>
                {phase.error.unreachable
                  ? 'The registry did not answer. Nothing was changed; try again.'
                  : phase.error.fields.map((item) => item.message).join(' ') || 'Nothing was changed.'}
              </AlertDescription>
            </Alert>
          ) : null}

          <div className="grid gap-2">
            <Label htmlFor="ack-note">
              Note <span className="text-muted-foreground font-normal">— optional</span>
            </Label>
            <Textarea
              id="ack-note"
              value={note}
              onChange={(event) => setNote(event.target.value)}
              placeholder="e.g. Visiting the post on Thursday to check the tablet."
              disabled={busy}
            />
          </div>
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button onClick={() => void submit()} disabled={busy}>
            {busy ? <Spinner /> : null}
            Acknowledge
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function LoadingList() {
  return (
    <Card aria-busy>
      <CardContent className="space-y-3 pt-6">
        <Skeleton className="h-4 w-48" />
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-10 w-full" />
      </CardContent>
    </Card>
  )
}

function Failure({ error, onRetry }: { error: NcbrsError; onRetry: () => void }) {
  return (
    <Empty className="border">
      <EmptyHeader>
        <EmptyMedia variant="icon">
          {error.unreachable ? <TriangleAlert /> : <CircleAlert />}
        </EmptyMedia>
        <EmptyTitle>{error.title}</EmptyTitle>
        <EmptyDescription>
          {error.unreachable
            ? 'The registry did not answer. Check the connection before trying again.'
            : error.fields.map((item) => item.message).join(' ') || 'The alert queue could not be loaded.'}
        </EmptyDescription>
      </EmptyHeader>
      <EmptyContent>
        <Button variant="outline" onClick={onRetry}>
          Try again
        </Button>
      </EmptyContent>
    </Empty>
  )
}
