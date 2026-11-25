import { type FormEvent, useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router'
import { useAuth } from 'react-oidc-context'
import { CircleAlert, FileCheck } from 'lucide-react'
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
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Spinner } from '@/components/ui/spinner'
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
import { WebChannelDeviceId } from '@/auth/channel'
import { realmRoles } from '@/auth/claims'
import { satisfies } from '@/auth/roles'
import { PageHeader } from '@/shell/PageHeader'
import { formatDate } from '@/records/RecordDetail'
import { ageInWords, QueueFailure, QueueSkeleton } from '@/review/queueParts'

type Item = components['schemas']['CertificateQueueItem']
type Stage = components['schemas']['CertificateQueueStage']
type Recipient = NonNullable<components['schemas']['CertificateRecipient']>
type Facility = components['schemas']['FacilityResponse']

const All = 'all'

export const StageLabels: Record<Stage, string> = {
  ToIssue: 'To issue and print',
  ToHandOver: 'Printed, to hand over',
  AwaitingNumber: 'Waiting for its number',
  AwaitingVerification: 'Waiting for verification',
}

/** Why a family is still waiting, in the words a registrar would say to them. */
const StageNotes: Record<Stage, string> = {
  ToIssue: 'Nothing stands in the way.',
  ToHandOver: 'Signed and printed; the family has not collected it.',
  AwaitingNumber: 'Registered offline under a provisional number. The registry numbers it at the next sync.',
  AwaitingVerification: 'Registered late. A district registrar must verify the evidence first.',
}

const Recipients: { value: Recipient; label: string }[] = [
  { value: 'Mother', label: 'The mother' },
  { value: 'Father', label: 'The father' },
  { value: 'Guardian', label: 'A guardian' },
  { value: 'OtherRelative', label: 'Another relative' },
  { value: 'Self', label: 'The person themselves' },
]

/**
 * Births whose certificate has not reached the family (plan §17 item 32).
 *
 * **Issuing is not delivering.** At a post offline for weeks the family leaves
 * with a slip, because only the registry signs a certificate; it is printed
 * later somewhere with signal and has to find its way back. Without this list,
 * a birth never certified and a certificate printed and left on a desk both
 * looked like nothing at all.
 *
 * Facility staff see their own facility, a district officer their county, the
 * Ministry the country; the registry decides which, and hiding the facility
 * filter from facility staff is only a courtesy. **Oldest first**, because the
 * family who has waited longest is first.
 */
export function CertificateQueue() {
  const api = useApiClient()
  const auth = useAuth()
  const oversees = satisfies(realmRoles(auth.user), 'CanEnrolDevices')

  const [rows, setRows] = useState<Item[] | null>(null)
  const [total, setTotal] = useState(0)
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)

  const [stage, setStage] = useState<Stage | typeof All>(All)
  const [facilities, setFacilities] = useState<Facility[]>([])
  const [facilityId, setFacilityId] = useState<string>(All)
  const [handingOver, setHandingOver] = useState<Item | null>(null)

  const load = useCallback(
    async (after: string | null, chosenStage: Stage | typeof All, facility: string) => {
      if (after) {
        setLoadingMore(true)
      } else {
        setLoading(true)
      }
      setError(null)

      try {
        const { data, error: failure, response } = await api.GET('/api/certificate-queue', {
          params: {
            query: {
              limit: 25,
              ...(after ? { after } : {}),
              ...(chosenStage !== All ? { stage: chosenStage } : {}),
              ...(facility !== All ? { facilityId: facility } : {}),
            },
          },
        })

        if (failure || !response.ok) {
          setError(toNcbrsError(failure, response.status))
          return
        }

        const page = data?.data
        setRows((current) => (after ? [...(current ?? []), ...(page?.items ?? [])] : (page?.items ?? [])))
        setTotal(page?.total ?? 0)
        setNextCursor(page?.nextCursor ?? null)
      } catch (cause) {
        setError(unreachableError(cause))
      } finally {
        setLoading(false)
        setLoadingMore(false)
      }
    },
    [api],
  )

  useEffect(() => {
    if (!oversees) {
      return
    }

    let cancelled = false
    void (async () => {
      try {
        const { data, response } = await api.GET('/api/facilities', { params: { query: { limit: 200 } } })
        if (!cancelled && response.ok) {
          setFacilities(data?.data?.items ?? [])
        }
      } catch {
        // The filter is a convenience; the list works without it.
      }
    })()

    return () => {
      cancelled = true
    }
  }, [api, oversees])

  useEffect(() => {
    void load(null, stage, facilityId)
  }, [load, stage, facilityId])

  const removeRow = useCallback((birthRecordId: string) => {
    setRows((current) => current?.filter((row) => row.birthRecordId !== birthRecordId) ?? null)
    setTotal((current) => Math.max(0, current - 1))
  }, [])

  return (
    <div className="mx-auto w-full max-w-5xl space-y-6">
      <PageHeader
        title="Certificates to hand out"
        description="Births whose certificate has not yet reached the family: to issue and print, to hand over, or waiting on the registry."
      />

      <div className="flex flex-wrap items-end gap-4">
        <div className="grid gap-2">
          <Label htmlFor="queue-stage-filter">Stage</Label>
          <Select value={stage} onValueChange={(value) => setStage(value as Stage | typeof All)}>
            <SelectTrigger id="queue-stage-filter" className="w-56">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={All}>Every stage</SelectItem>
              {(Object.keys(StageLabels) as Stage[]).map((value) => (
                <SelectItem key={value} value={value}>
                  {StageLabels[value]}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>

        {oversees ? (
          <div className="grid gap-2">
            <Label htmlFor="queue-facility-filter">Facility</Label>
            <Select value={facilityId} onValueChange={setFacilityId}>
              <SelectTrigger id="queue-facility-filter" className="w-64">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={All}>All facilities</SelectItem>
                {facilities.map((facility) => (
                  <SelectItem key={facility.facilityId} value={facility.facilityId}>
                    {facility.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        ) : null}
      </div>

      {loading && rows === null ? <QueueSkeleton /> : null}

      {error ? (
        <QueueFailure
          error={error}
          onRetry={() => void load(null, stage, facilityId)}
          fallback="The list of certificates could not be loaded."
        />
      ) : null}

      {rows !== null && !error ? (
        rows.length === 0 ? (
          <Empty className="border">
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <FileCheck />
              </EmptyMedia>
              <EmptyTitle>Nothing waiting</EmptyTitle>
              <EmptyDescription>
                {stage === All && facilityId === All
                  ? 'Every registered birth here has its certificate in the family’s hands.'
                  : 'Nothing at this stage. Clear the filters to see the rest.'}
              </EmptyDescription>
            </EmptyHeader>
          </Empty>
        ) : (
          <Card>
            <CardContent className="pt-6">
              <p className="text-muted-foreground mb-3 text-sm">
                {total === 1 ? '1 birth' : `${total} births`} waiting, oldest first.
              </p>

              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Record</TableHead>
                    <TableHead>Born</TableHead>
                    {oversees ? <TableHead>Facility</TableHead> : null}
                    <TableHead>Stage</TableHead>
                    <TableHead>Registered</TableHead>
                    <TableHead className="text-right">Next</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {rows.map((row) => (
                    <TableRow key={row.birthRecordId}>
                      <TableCell className="align-top">
                        <div className="font-medium">{row.childFullName}</div>
                        <Link
                          to={`/records?brn=${encodeURIComponent(row.brn)}`}
                          className="text-muted-foreground font-mono text-xs underline-offset-4 hover:underline"
                        >
                          {row.brn}
                        </Link>
                      </TableCell>
                      <TableCell className="align-top whitespace-nowrap">{formatDate(row.dateOfBirth)}</TableCell>
                      {oversees ? <TableCell className="align-top">{row.facilityName}</TableCell> : null}
                      <TableCell className="align-top">
                        <Badge variant={row.stage === 'ToIssue' || row.stage === 'ToHandOver' ? 'default' : 'secondary'}>
                          {StageLabels[row.stage]}
                        </Badge>
                        <span className="text-muted-foreground mt-1 block text-xs">
                          {row.stage === 'ToHandOver' && row.certificateIssuedAtUtc
                            ? `Printed ${ageInWords(row.certificateIssuedAtUtc)}.`
                            : StageNotes[row.stage]}
                        </span>
                      </TableCell>
                      <TableCell
                        className="text-muted-foreground align-top whitespace-nowrap"
                        title={formatDate(row.registeredAtUtc)}
                      >
                        {ageInWords(row.registeredAtUtc)}
                      </TableCell>
                      <TableCell className="text-right align-top">
                        {row.stage === 'ToIssue' ? (
                          <Button asChild variant="outline" size="sm">
                            <Link to={`/records/certificate?brn=${encodeURIComponent(row.brn)}`}>Issue</Link>
                          </Button>
                        ) : row.stage === 'ToHandOver' ? (
                          <Button variant="outline" size="sm" onClick={() => setHandingOver(row)}>
                            Hand over
                          </Button>
                        ) : null}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>

              {nextCursor ? (
                <div className="mt-4 flex justify-center">
                  <Button
                    variant="outline"
                    onClick={() => void load(nextCursor, stage, facilityId)}
                    disabled={loadingMore}
                  >
                    {loadingMore ? <Spinner /> : null}
                    Show more
                  </Button>
                </div>
              ) : null}
            </CardContent>
          </Card>
        )
      ) : null}

      {handingOver ? (
        <HandOverDialog
          row={handingOver}
          onClose={() => setHandingOver(null)}
          onDone={(birthRecordId) => {
            removeRow(birthRecordId)
            setHandingOver(null)
          }}
        />
      ) : null}
    </div>
  )
}

/**
 * Records who took the certificate away. Who received it is asked, never
 * assumed: most often it is the mother, which is exactly why a default would
 * record her when it was someone else.
 */
function HandOverDialog({
  row,
  onClose,
  onDone,
}: {
  row: Item
  onClose: () => void
  onDone: (birthRecordId: string) => void
}) {
  const api = useApiClient()
  const [recipient, setRecipient] = useState<Recipient | ''>('')
  const [error, setError] = useState<NcbrsError | null>(null)
  const [submitting, setSubmitting] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (recipient === '') {
      return
    }

    setSubmitting(true)
    setError(null)

    try {
      const { error: failure, response } = await api.POST('/api/BirthRecords/{brn}/certificate/handover', {
        params: { path: { brn: row.brn } },
        body: { data: { deviceId: WebChannelDeviceId, receivedBy: recipient } },
      })

      if (response.ok) {
        onDone(row.birthRecordId)
        return
      }

      // Already handed over is the outcome wanted, recorded by someone else
      // first, so the row leaves the list. Any other refusal (the certificate
      // withdrawn meanwhile, say) is shown: the family's paper may not stand.
      const refusal = toNcbrsError(failure, response.status)
      if (response.status === 409 && refusal.title === 'Already handed over.') {
        onDone(row.birthRecordId)
        return
      }

      setError(refusal)
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
          <DialogTitle>Hand over the certificate</DialogTitle>
          <DialogDescription>
            {row.childFullName}, <span className="font-mono">{row.brn}</span>. Record this once the
            family has the printed certificate in their hands.
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
            <Label htmlFor="handover-recipient">Who received it</Label>
            <Select value={recipient} onValueChange={(value) => setRecipient(value as Recipient)}>
              <SelectTrigger id="handover-recipient" className="w-full">
                <SelectValue placeholder="Choose" />
              </SelectTrigger>
              <SelectContent>
                {Recipients.map((option) => (
                  <SelectItem key={option.value} value={option.value}>
                    {option.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" disabled={recipient === '' || submitting}>
              {submitting ? <Spinner /> : null}
              Record handover
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
