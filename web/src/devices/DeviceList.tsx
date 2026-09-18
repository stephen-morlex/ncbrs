import { useCallback, useEffect, useState } from 'react'
import { CircleAlert, Smartphone, TriangleAlert } from 'lucide-react'
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
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
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
import { EnrolDeviceDialog } from './EnrolDeviceDialog'

type Device = components['schemas']['DeviceResponse']
type DeviceStatus = components['schemas']['DeviceStatus']
type Facility = components['schemas']['FacilityResponse']

const AllFacilities = 'all'

type Action = 'suspend' | 'reinstate' | 'revoke'

/**
 * The devices permitted to sync — the tablets and terminals that register
 * births — and their standing.
 *
 * **A device that has never reported is the failure worth seeing.** One
 * enrolled weeks ago with no `lastSeenAtUtc` is a deployment that failed at
 * handover; without this view its post looks like a quiet area rather than a
 * broken one. So a never-reported device is called out, not left to read as a
 * blank cell.
 *
 * Suspension is reversible — a mislaid tablet usually reappears, and a district
 * made to re-enrol every time stops reporting them missing. Revocation is not:
 * there is no un-revoke, and the remedy for a mistake is a fresh enrolment with
 * a fresh key, which leaves both acts visible.
 */
export function DeviceList() {
  const api = useApiClient()

  const [devices, setDevices] = useState<Device[] | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(true)

  const [facilities, setFacilities] = useState<Facility[]>([])
  const [facilityId, setFacilityId] = useState<string>(AllFacilities)

  const [acting, setActing] = useState<{ device: Device; action: Action } | null>(null)
  const [enrolling, setEnrolling] = useState(false)

  const load = useCallback(
    async (facility: string) => {
      setLoading(true)
      setError(null)

      try {
        const { data, error: failure, response } = await api.GET('/api/devices', {
          params: {
            query: facility !== AllFacilities ? { facilityId: facility } : {},
          },
        })

        if (failure || !response.ok) {
          setError(toNcbrsError(failure, response.status))
          return
        }

        setDevices(data?.data ?? [])
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
        // The names and the filter are a convenience; the list works without them.
      }
    })()

    return () => {
      cancelled = true
    }
  }, [api])

  useEffect(() => {
    void load(facilityId)
  }, [load, facilityId])

  const facilityName = useCallback(
    (id: string) => facilities.find((facility) => facility.facilityId === id)?.name ?? id,
    [facilities],
  )

  // A status change comes back as the updated device; swap it in place rather
  // than refetch the whole list.
  const applyUpdated = useCallback((updated: Device) => {
    setDevices((current) =>
      current?.map((device) => (device.deviceId === updated.deviceId ? updated : device)) ?? null,
    )
  }, [])

  return (
    <div className="mx-auto w-full max-w-5xl space-y-6">
      <PageHeader
        title="Devices"
        description="The tablets and terminals permitted to register and sync births — and which of them have gone quiet."
      />

      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="grid gap-2">
          <Label htmlFor="device-facility">Facility</Label>
          <Select value={facilityId} onValueChange={setFacilityId}>
            <SelectTrigger id="device-facility" className="w-64">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={AllFacilities}>All facilities</SelectItem>
              {facilities.map((facility) => (
                <SelectItem key={facility.facilityId} value={facility.facilityId}>
                  {facility.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>

        <Button onClick={() => setEnrolling(true)} disabled={facilities.length === 0}>
          <Smartphone />
          Enrol a device
        </Button>
      </div>

      {loading && devices === null ? <LoadingList /> : null}

      {error ? <Failure error={error} onRetry={() => void load(facilityId)} /> : null}

      {devices !== null && !error ? (
        devices.length === 0 ? (
          <Empty className="border">
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <Smartphone />
              </EmptyMedia>
              <EmptyTitle>No devices enrolled</EmptyTitle>
              <EmptyDescription>
                A facility registering births with no enrolled device is worth checking — either
                nothing is deployed there, or records are arriving unattributed.
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
                    <TableHead>Facility</TableHead>
                    <TableHead>Status</TableHead>
                    <TableHead>Last reported</TableHead>
                    <TableHead className="text-right">Actions</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {devices.map((device) => (
                    <TableRow key={device.deviceId}>
                      <TableCell className="align-top">
                        <div className="font-mono text-sm">{device.deviceId}</div>
                        {device.label ? (
                          <div className="text-muted-foreground text-xs">{device.label}</div>
                        ) : null}
                      </TableCell>
                      <TableCell className="align-top">{facilityName(device.facilityId)}</TableCell>
                      <TableCell className="align-top">
                        <StatusBadge status={device.status} reason={device.statusReason} />
                      </TableCell>
                      <TableCell className="align-top">
                        <LastReported device={device} />
                      </TableCell>
                      <TableCell className="text-right align-top">
                        <Actions device={device} onAct={(action) => setActing({ device, action })} />
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </CardContent>
          </Card>
        )
      ) : null}

      {enrolling ? (
        <EnrolDeviceDialog
          facilities={facilities}
          defaultFacilityId={facilityId !== AllFacilities ? facilityId : (facilities[0]?.facilityId ?? '')}
          onClose={() => setEnrolling(false)}
          onEnrolled={() => {
            setEnrolling(false)
            void load(facilityId)
          }}
        />
      ) : null}

      {acting ? (
        <ChangeStatusDialog
          device={acting.device}
          action={acting.action}
          onClose={() => setActing(null)}
          onDone={(updated) => {
            applyUpdated(updated)
            setActing(null)
          }}
        />
      ) : null}
    </div>
  )
}

function StatusBadge({ status, reason }: { status: DeviceStatus; reason: string | null }) {
  const title = reason ?? undefined
  if (status === 'Revoked') {
    return (
      <Badge variant="destructive" title={title}>
        revoked
      </Badge>
    )
  }
  if (status === 'Suspended') {
    return (
      <Badge variant="outline" title={title}>
        suspended
      </Badge>
    )
  }
  return <Badge variant="secondary">in service</Badge>
}

/**
 * Never-reported is a different fact from a stale last-sync, and is shown as
 * such: it means a device the Ministry issued that has not once been heard
 * from — the deployment that failed at handover.
 */
function LastReported({ device }: { device: Device }) {
  if (device.lastSeenAtUtc) {
    return <span className="whitespace-nowrap">{formatDate(device.lastSeenAtUtc)}</span>
  }

  return (
    <span className="text-muted-foreground">
      Never reported
      <span className="block text-xs">enrolled {formatDate(device.enrolledAtUtc)}</span>
    </span>
  )
}

function Actions({ device, onAct }: { device: Device; onAct: (action: Action) => void }) {
  if (device.status === 'Revoked') {
    return <span className="text-muted-foreground text-xs">withdrawn</span>
  }

  return (
    <div className="flex justify-end gap-2">
      {device.status === 'Suspended' ? (
        <Button variant="outline" size="sm" onClick={() => onAct('reinstate')}>
          Reinstate
        </Button>
      ) : (
        <Button variant="outline" size="sm" onClick={() => onAct('suspend')}>
          Suspend
        </Button>
      )}
      <Button variant="outline" size="sm" className="text-destructive" onClick={() => onAct('revoke')}>
        Revoke
      </Button>
    </div>
  )
}

const ActionCopy: Record<Action, { title: string; verb: string; blurb: string; destructive: boolean }> = {
  suspend: {
    title: 'Suspend this device',
    verb: 'Suspend',
    blurb: 'Bars the device from syncing until it is reinstated. Reversible — use this for a mislaid tablet.',
    destructive: false,
  },
  reinstate: {
    title: 'Reinstate this device',
    verb: 'Reinstate',
    blurb: 'Returns a suspended device to service.',
    destructive: false,
  },
  revoke: {
    title: 'Revoke this device',
    verb: 'Revoke',
    blurb: 'Withdraws the device permanently. There is no un-revoke — a device revoked in error is replaced by a fresh enrolment with a fresh key.',
    destructive: true,
  },
}

type Phase =
  | { kind: 'confirm' }
  | { kind: 'submitting' }
  | { kind: 'error'; error: NcbrsError }

function ChangeStatusDialog({
  device,
  action,
  onClose,
  onDone,
}: {
  device: Device
  action: Action
  onClose: () => void
  onDone: (updated: Device) => void
}) {
  const api = useApiClient()
  const copy = ActionCopy[action]

  const [reason, setReason] = useState('')
  const [phase, setPhase] = useState<Phase>({ kind: 'confirm' })
  const [reasonMissing, setReasonMissing] = useState(false)

  const busy = phase.kind === 'submitting'

  const submit = useCallback(async () => {
    // The server requires the reason so the next officer can tell why a device
    // was barred; refusing here keeps that legible.
    if (reason.trim().length === 0) {
      setReasonMissing(true)
      return
    }

    setReasonMissing(false)
    setPhase({ kind: 'submitting' })

    const body = { params: { path: { deviceId: device.deviceId } }, body: { data: { reason: reason.trim() } } }

    try {
      const result =
        action === 'suspend'
          ? await api.POST('/api/devices/{deviceId}/suspend', body)
          : action === 'reinstate'
            ? await api.POST('/api/devices/{deviceId}/reinstate', body)
            : await api.POST('/api/devices/{deviceId}/revoke', body)

      const { data, error: failure, response } = result

      if (response.ok && data?.data) {
        onDone(data.data)
        return
      }

      setPhase({ kind: 'error', error: toNcbrsError(failure, response.status) })
    } catch (cause) {
      setPhase({ kind: 'error', error: unreachableError(cause) })
    }
  }, [api, action, device.deviceId, reason, onDone])

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : onClose())}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{copy.title}</DialogTitle>
          <DialogDescription className="font-mono">{device.deviceId}</DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <p className="text-muted-foreground text-sm">{copy.blurb}</p>

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
            <Label htmlFor="device-reason">Reason</Label>
            <Textarea
              id="device-reason"
              value={reason}
              onChange={(event) => {
                setReason(event.target.value)
                if (reasonMissing) {
                  setReasonMissing(false)
                }
              }}
              placeholder="Why, so the next officer can tell what happened."
              aria-invalid={reasonMissing || undefined}
              aria-describedby={reasonMissing ? 'device-reason-error' : undefined}
              disabled={busy}
            />
            {reasonMissing ? (
              <p id="device-reason-error" className="text-destructive text-sm">
                A reason is required. It is kept with the device's history.
              </p>
            ) : null}
          </div>
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button
            variant={copy.destructive ? 'destructive' : 'default'}
            onClick={() => void submit()}
            disabled={busy}
          >
            {busy ? <Spinner /> : null}
            {copy.verb}
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
            : error.fields.map((item) => item.message).join(' ') || 'The device list could not be loaded.'}
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
