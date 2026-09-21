import { useCallback, useEffect, useState } from 'react'
import { CircleAlert, Clock, TriangleAlert } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
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
import { ageInWords, QueueFailure, QueueSkeleton } from './queueParts'

type LateRegistration = components['schemas']['PendingLateRegistrationResponse']
type EvidenceType = components['schemas']['LateRegistrationEvidenceType']
type Facility = components['schemas']['FacilityResponse']

const AllFacilities = 'all'

/**
 * The evidence types read as English, not enum tokens — a registrar knows
 * "antenatal card", not `AntenatalOrDeliveryCard`.
 */
const EvidenceLabels: Record<EvidenceType, string> = {
  HealthFacilityRecord: 'Health facility record',
  AntenatalOrDeliveryCard: 'Antenatal or delivery card',
  ImmunisationRecord: 'Immunisation record',
  BirthAttendantAttestation: 'Birth attendant attestation',
  ReligiousRecord: 'Religious record',
  SchoolRecord: 'School record',
  SwornAffidavit: 'Sworn affidavit',
  CourtOrder: 'Court order',
}

/**
 * Births registered after the statutory window, waiting on a district
 * registrar to verify the evidence behind the claimed date of birth.
 *
 * **Verification withholds the certificate, not the registration.** The record
 * and its BRN already exist — a child registered late is still a child who
 * exists. Approving is what releases the certificate; refusing leaves the
 * registration standing but uncertifiable, and the refusal is kept so the same
 * claim cannot simply be refiled as though it had never been seen.
 *
 * **The verifier is not the filer.** Backdating a birth is the fraud this
 * deters — a date of birth decides school entry, the age of majority, marriage
 * and pension, with nobody contemporaneous left to contradict a claim made
 * years later — so the server refuses a verification by the registrar who
 * filed it, and the queue names the filer.
 *
 * **Oldest first, because age is a family without a certificate.**
 */
export function LateRegistrationQueue() {
  const api = useApiClient()

  const [rows, setRows] = useState<LateRegistration[] | null>(null)
  const [total, setTotal] = useState(0)
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)

  const [facilities, setFacilities] = useState<Facility[]>([])
  const [facilityId, setFacilityId] = useState<string>(AllFacilities)

  const [reviewing, setReviewing] = useState<LateRegistration | null>(null)

  const load = useCallback(
    async (after: string | null, facility: string) => {
      if (after) {
        setLoadingMore(true)
      } else {
        setLoading(true)
      }
      setError(null)

      try {
        const { data, error: failure, response } = await api.GET('/api/late-registrations/pending', {
          params: {
            query: {
              limit: 25,
              ...(after ? { after } : {}),
              ...(facility !== AllFacilities ? { facilityId: facility } : {}),
            },
          },
        })

        if (failure || !response.ok) {
          setError(toNcbrsError(failure, response.status))
          return
        }

        const page = data?.data
        setRows((current) =>
          after ? [...(current ?? []), ...(page?.items ?? [])] : (page?.items ?? []),
        )
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
        // The filter is a convenience; the queue works without it.
      }
    })()

    return () => {
      cancelled = true
    }
  }, [api])

  useEffect(() => {
    void load(null, facilityId)
  }, [load, facilityId])

  const removeRow = useCallback((lateRegistrationId: string) => {
    setRows((current) => current?.filter((row) => row.lateRegistrationId !== lateRegistrationId) ?? null)
    setTotal((current) => Math.max(0, current - 1))
  }, [])

  return (
    <div className="mx-auto w-full max-w-5xl space-y-6">
      <PageHeader
        title="Late registrations"
        description="Births registered after the statutory window, held until the evidence behind the date of birth is verified."
      />

      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="grid gap-2">
          <Label htmlFor="late-facility-filter">Facility</Label>
          <Select value={facilityId} onValueChange={setFacilityId}>
            <SelectTrigger id="late-facility-filter" className="w-64">
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
      </div>

      {loading && rows === null ? <QueueSkeleton /> : null}

      {error ? (
        <QueueFailure
          error={error}
          onRetry={() => void load(null, facilityId)}
          fallback="The late-registration queue could not be loaded."
        />
      ) : null}

      {rows !== null && !error ? (
        rows.length === 0 ? (
          <Empty className="border">
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <Clock />
              </EmptyMedia>
              <EmptyTitle>No late registrations to verify</EmptyTitle>
              <EmptyDescription>
                {facilityId === AllFacilities
                  ? 'Every late registration in your district has been verified. Each cleared one is a family that can now be issued a certificate.'
                  : 'This facility has none waiting. Clear the filter to see the rest of the district.'}
              </EmptyDescription>
            </EmptyHeader>
          </Empty>
        ) : (
          <Card>
            <CardContent className="pt-6">
              <p className="text-muted-foreground mb-3 text-sm">
                {plural(total)} waiting, oldest first.
              </p>

              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Record</TableHead>
                    <TableHead>Born</TableHead>
                    <TableHead>Late by</TableHead>
                    <TableHead>Evidence</TableHead>
                    <TableHead>Declarant</TableHead>
                    <TableHead>Waiting</TableHead>
                    <TableHead className="text-right">Verify</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {rows.map((row) => (
                    <TableRow key={row.lateRegistrationId}>
                      <TableCell className="align-top">
                        <div className="font-medium">{row.childFullName}</div>
                        <div className="text-muted-foreground font-mono text-xs">{row.brn}</div>
                      </TableCell>
                      <TableCell className="align-top whitespace-nowrap">
                        {formatDate(row.dateOfBirth)}
                      </TableCell>
                      <TableCell className="align-top whitespace-nowrap">
                        {daysLateInWords(row.daysLate)}
                        {/* The window in force when it was filed, kept beside the
                            lateness because the statutory window is set in law
                            and a verdict without it looks arbitrary. */}
                        <span className="text-muted-foreground block text-xs">
                          window was {row.windowDaysAtFiling} days
                        </span>
                      </TableCell>
                      <TableCell className="align-top">
                        {EvidenceLabels[row.evidenceType]}
                        {row.evidenceReference ? (
                          <span className="text-muted-foreground block font-mono text-xs">
                            {row.evidenceReference}
                          </span>
                        ) : null}
                      </TableCell>
                      <TableCell className="align-top">
                        {row.declarantName}
                        <span className="text-muted-foreground block text-xs">
                          {row.declarantRelationship}
                        </span>
                      </TableCell>
                      <TableCell
                        className="text-muted-foreground align-top whitespace-nowrap"
                        title={formatDate(row.submittedAtUtc)}
                      >
                        {ageInWords(row.submittedAtUtc)}
                      </TableCell>
                      <TableCell className="text-right align-top">
                        <Button variant="outline" size="sm" onClick={() => setReviewing(row)}>
                          Verify
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>

              {nextCursor ? (
                <div className="mt-4 flex justify-center">
                  <Button
                    variant="outline"
                    onClick={() => void load(nextCursor, facilityId)}
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

      {reviewing ? (
        <VerifyDialog
          row={reviewing}
          onClose={() => setReviewing(null)}
          onResolved={(lateRegistrationId) => {
            removeRow(lateRegistrationId)
            setReviewing(null)
          }}
        />
      ) : null}
    </div>
  )
}

type Phase =
  | { kind: 'confirm' }
  | { kind: 'submitting' }
  | { kind: 'approved' }
  | { kind: 'refused' }
  | { kind: 'gone' }
  | { kind: 'error'; error: NcbrsError }

/**
 * Verify one late registration.
 *
 * The reviewer confirms the evidence supports the claimed date of birth, then
 * either **verifies** it — which releases the certificate — or **refuses** it,
 * which leaves the registration standing but keeps the certificate withheld.
 * A note is required either way: an unexplained approval is the same as no
 * verification at all, which is exactly what this process exists to prevent,
 * and a refusal without a reason cannot stop the same claim being refiled.
 */
function VerifyDialog({
  row,
  onClose,
  onResolved,
}: {
  row: LateRegistration
  onClose: () => void
  onResolved: (lateRegistrationId: string) => void
}) {
  const api = useApiClient()

  const [note, setNote] = useState('')
  const [phase, setPhase] = useState<Phase>({ kind: 'confirm' })
  const [noteMissing, setNoteMissing] = useState(false)

  const busy = phase.kind === 'submitting'
  const resolved = phase.kind === 'approved' || phase.kind === 'refused' || phase.kind === 'gone'

  const review = useCallback(
    async (approve: boolean) => {
      if (note.trim().length === 0) {
        setNoteMissing(true)
        return
      }

      setNoteMissing(false)
      setPhase({ kind: 'submitting' })

      try {
        const { error: failure, response } = await api.POST(
          '/api/late-registrations/{lateRegistrationId}/review',
          {
            params: { path: { lateRegistrationId: row.lateRegistrationId } },
            body: { data: { approve, note: note.trim() } },
          },
        )

        if (response.ok) {
          setPhase(approve ? { kind: 'approved' } : { kind: 'refused' })
          return
        }

        const ncbrs = toNcbrsError(failure, response.status)

        if (ncbrs.status === 409 || ncbrs.status === 404) {
          setPhase({ kind: 'gone' })
          return
        }

        setPhase({ kind: 'error', error: ncbrs })
      } catch (cause) {
        setPhase({ kind: 'error', error: unreachableError(cause) })
      }
    },
    [api, note, row.lateRegistrationId],
  )

  const close = useCallback(() => {
    if (resolved) {
      onResolved(row.lateRegistrationId)
    } else {
      onClose()
    }
  }, [resolved, onResolved, onClose, row.lateRegistrationId])

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : close())}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Verify this late registration</DialogTitle>
          <DialogDescription>
            {row.childFullName} · <span className="font-mono">{row.brn}</span>
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <dl className="grid grid-cols-1 gap-x-4 gap-y-3 text-sm sm:grid-cols-2">
            <div>
              <dt className="text-muted-foreground text-xs">Date of birth</dt>
              <dd>{formatDate(row.dateOfBirth)}</dd>
            </div>
            <div>
              <dt className="text-muted-foreground text-xs">Late by</dt>
              <dd>
                {daysLateInWords(row.daysLate)}{' '}
                <span className="text-muted-foreground">(window {row.windowDaysAtFiling} days)</span>
              </dd>
            </div>
            <div>
              <dt className="text-muted-foreground text-xs">Evidence</dt>
              <dd>
                {EvidenceLabels[row.evidenceType]}
                {row.evidenceReference ? (
                  <span className="text-muted-foreground block font-mono text-xs">
                    {row.evidenceReference}
                  </span>
                ) : null}
              </dd>
            </div>
            <div>
              <dt className="text-muted-foreground text-xs">Declarant</dt>
              <dd>
                {row.declarantName}{' '}
                <span className="text-muted-foreground">({row.declarantRelationship})</span>
              </dd>
            </div>
          </dl>

          <p className="text-muted-foreground text-sm">
            Filed by {row.submittedByRegistrarName}, {ageInWords(row.submittedAtUtc)}.
          </p>

          {phase.kind === 'confirm' || phase.kind === 'submitting' || phase.kind === 'error' ? (
            <>
              {phase.kind === 'error' ? (
                <Alert variant="destructive">
                  <CircleAlert />
                  <AlertTitle>{phase.error.title}</AlertTitle>
                  <AlertDescription>
                    {phase.error.unreachable
                      ? 'The registry did not answer. Nothing was changed; try again.'
                      : phase.error.fields.map((item) => item.message).join(' ') ||
                        'Nothing was changed.'}
                  </AlertDescription>
                </Alert>
              ) : null}

              <div className="grid gap-2">
                <Label htmlFor="verify-note">
                  Note{' '}
                  <span className="text-muted-foreground font-normal">
                    — what you checked, or why you are refusing (required)
                  </span>
                </Label>
                <Textarea
                  id="verify-note"
                  value={note}
                  onChange={(event) => {
                    setNote(event.target.value)
                    if (noteMissing) {
                      setNoteMissing(false)
                    }
                  }}
                  placeholder="e.g. Antenatal card seen; date of birth matches the delivery record."
                  aria-invalid={noteMissing || undefined}
                  aria-describedby={noteMissing ? 'verify-note-error' : undefined}
                  disabled={busy}
                />
                {noteMissing ? (
                  <p id="verify-note-error" className="text-destructive text-sm">
                    A reason is required. An unexplained approval is no verification at all.
                  </p>
                ) : null}
              </div>
            </>
          ) : null}

          {phase.kind === 'approved' ? (
            <Alert>
              <AlertTitle>Verified</AlertTitle>
              <AlertDescription>
                The evidence is accepted and the certificate can now be issued. Your note is kept
                as part of the record's history.
              </AlertDescription>
            </Alert>
          ) : null}

          {phase.kind === 'refused' ? (
            <Alert>
              <AlertTitle>Verification refused</AlertTitle>
              <AlertDescription>
                The registration stands — the child still exists in the register — but no
                certificate will be issued. The refusal and its reason are kept, so the same claim
                cannot be refiled as though it had never been seen.
              </AlertDescription>
            </Alert>
          ) : null}

          {phase.kind === 'gone' ? (
            <Alert>
              <TriangleAlert />
              <AlertTitle>Already reviewed</AlertTitle>
              <AlertDescription>
                Another registrar has already decided this one. It has left the queue.
              </AlertDescription>
            </Alert>
          ) : null}
        </div>

        <DialogFooter>
          {resolved ? (
            <Button onClick={close}>Done</Button>
          ) : (
            <>
              <Button
                variant="outline"
                onClick={() => void review(false)}
                disabled={busy}
                className="text-destructive"
              >
                Refuse
              </Button>
              <Button onClick={() => void review(true)} disabled={busy}>
                {busy ? <Spinner /> : null}
                Verify
              </Button>
            </>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

/** "12 days late", but "on the window" reads better than "0 days late". */
function daysLateInWords(days: number): string {
  if (days <= 0) {
    return 'on the window'
  }
  return `${days.toLocaleString()} ${days === 1 ? 'day' : 'days'} late`
}

function plural(count: number): string {
  return `${count.toLocaleString()} ${count === 1 ? 'registration' : 'registrations'}`
}
