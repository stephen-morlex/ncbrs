import { useCallback, useEffect, useState } from 'react'
import { CircleAlert, Inbox, TriangleAlert } from 'lucide-react'
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

type PendingAmendment = components['schemas']['PendingAmendmentResponse']
type AmendedField = components['schemas']['AmendedFieldResponse']
type Facility = components['schemas']['FacilityResponse']
type ReviewResponse = components['schemas']['ReviewAmendmentResponse']

const AllFacilities = 'all'

/**
 * The queue a district officer works through: corrections that change how the
 * register identifies a person, waiting on a second pair of eyes.
 *
 * **Only the approval track reaches this screen.** Clinical measurements — a
 * birth weight, a gestational age — take effect the moment they are filed and
 * are never queued. What waits here is a name, a date of birth, a sex, or a
 * parent's name: the fields that decide who the record is about, which the
 * submitter is deliberately not allowed to change alone.
 *
 * **Oldest first, because age is the thing worth seeing.** A correction left
 * sitting here is a family holding a certificate the register already knows is
 * wrong. The server orders the queue; this screen keeps that order and shows
 * each row's age plainly.
 *
 * **The submitter is named on every row.** Approval is a separation-of-duties
 * control — the server refuses an approval by the registrar who filed it — and
 * showing who submitted a change is what lets a reviewer see, before they act,
 * whether the second pair of eyes is really theirs.
 */
export function AmendmentQueue() {
  const api = useApiClient()

  const [rows, setRows] = useState<PendingAmendment[] | null>(null)
  const [total, setTotal] = useState(0)
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)

  const [facilities, setFacilities] = useState<Facility[]>([])
  const [facilityId, setFacilityId] = useState<string>(AllFacilities)

  const [reviewing, setReviewing] = useState<PendingAmendment | null>(null)

  const load = useCallback(
    async (after: string | null, facility: string) => {
      if (after) {
        setLoadingMore(true)
      } else {
        setLoading(true)
      }
      setError(null)

      try {
        const { data, error: failure, response } = await api.GET('/api/amendments/pending', {
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

  // The facilities in scope, for the filter. A district officer sees only
  // their own district's facilities, so the dropdown is short and the filter
  // never widens what the queue query already scopes server-side.
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
        // The filter is a convenience; the queue works without it. A failure
        // here must not take the queue down with it.
      }
    })()

    return () => {
      cancelled = true
    }
  }, [api])

  // Loaded on arrival, and reloaded from the top whenever the filter changes —
  // a cursor drawn against one facility scope is meaningless against another.
  useEffect(() => {
    void load(null, facilityId)
  }, [load, facilityId])

  // A reviewed row leaves the queue without a round trip: the server already
  // told us it is resolved, and refetching would only prove what we know.
  const removeRow = useCallback((amendmentRequestId: string) => {
    setRows((current) => current?.filter((row) => row.amendmentRequestId !== amendmentRequestId) ?? null)
    setTotal((current) => Math.max(0, current - 1))
  }, [])

  return (
    <div className="mx-auto w-full max-w-5xl space-y-6">
      <PageHeader
        title="Amendment approvals"
        description="Corrections to a person's identity, held until a registrar who did not submit them signs off."
      />

      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="grid gap-2">
          <Label htmlFor="facility-filter">Facility</Label>
          <Select value={facilityId} onValueChange={setFacilityId}>
            <SelectTrigger id="facility-filter" className="w-64">
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

      {loading && rows === null ? <LoadingQueue /> : null}

      {error ? <Failure error={error} onRetry={() => void load(null, facilityId)} /> : null}

      {rows !== null && !error ? (
        rows.length === 0 ? (
          <Empty className="border">
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <Inbox />
              </EmptyMedia>
              <EmptyTitle>No corrections waiting</EmptyTitle>
              <EmptyDescription>
                {facilityId === AllFacilities
                  ? 'Every identity correction in your district has been reviewed. This is the queue you want to be empty.'
                  : 'This facility has no corrections waiting. Clear the filter to see the rest of the district.'}
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
                    <TableHead>Proposed change</TableHead>
                    <TableHead>Submitted by</TableHead>
                    <TableHead>Waiting</TableHead>
                    <TableHead className="text-right">Review</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {rows.map((row) => (
                    <TableRow key={row.amendmentRequestId}>
                      <TableCell className="align-top">
                        <div className="font-medium">{row.childFullName}</div>
                        <div className="text-muted-foreground font-mono text-xs">{row.brn}</div>
                        <div className="text-muted-foreground text-xs">{row.facilityName}</div>
                      </TableCell>
                      <TableCell className="align-top">
                        <ChangeList changes={row.changes} />
                      </TableCell>
                      <TableCell className="align-top">{row.submittedByRegistrarName}</TableCell>
                      <TableCell
                        className="text-muted-foreground align-top whitespace-nowrap"
                        title={formatDate(row.submittedAtUtc)}
                      >
                        {ageInWords(row.submittedAtUtc)}
                      </TableCell>
                      <TableCell className="text-right align-top">
                        <Button variant="outline" size="sm" onClick={() => setReviewing(row)}>
                          Review
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
        <ReviewDialog
          row={reviewing}
          onClose={() => setReviewing(null)}
          onResolved={(amendmentRequestId) => {
            removeRow(amendmentRequestId)
            setReviewing(null)
          }}
        />
      ) : null}
    </div>
  )
}

/**
 * The field-by-field diff, previous → new. Field names are shown as the
 * register names them, matching the amendment history on the record page — one
 * naming, so a reviewer reads the same word in both places.
 */
function ChangeList({ changes }: { changes: AmendedField[] }) {
  return (
    <ul className="space-y-1 text-sm">
      {changes.map((change) => (
        <li key={change.field}>
          <span className="font-medium">{change.field}</span>{' '}
          <span className="text-muted-foreground">{change.previousValue ?? '—'}</span>
          {' → '}
          <span>{change.newValue ?? '—'}</span>
        </li>
      ))}
    </ul>
  )
}

type Phase =
  | { kind: 'confirm' }
  | { kind: 'submitting' }
  | { kind: 'approved'; result: ReviewResponse }
  | { kind: 'refused' }
  // The record moved under the proposal. Kept distinct from every other
  // failure: the correction may still be right, only not against these values,
  // and it stays in the queue rather than being removed.
  | { kind: 'drift'; error: NcbrsError }
  | { kind: 'gone' }
  | { kind: 'error'; error: NcbrsError }

/**
 * Review one correction. The reviewer sees the full diff, then either approves
 * it or refuses it with a reason.
 *
 * **Approval is the moment the change takes effect** and any certificate it
 * contradicts is withdrawn — nothing downstream heard about the correction
 * until now. The dialog says so when it happens, because a reviewer approving a
 * change to a name or a date of birth is invalidating a document a family may
 * be holding.
 *
 * **A refusal must carry a reason** (the server requires it): a correction
 * turned down without one tells the next person nothing, and a refused change
 * is kept as history precisely so it can be understood later.
 */
function ReviewDialog({
  row,
  onClose,
  onResolved,
}: {
  row: PendingAmendment
  onClose: () => void
  onResolved: (amendmentRequestId: string) => void
}) {
  const api = useApiClient()

  const [note, setNote] = useState('')
  const [phase, setPhase] = useState<Phase>({ kind: 'confirm' })
  const [noteMissing, setNoteMissing] = useState(false)

  const busy = phase.kind === 'submitting'
  const resolved =
    phase.kind === 'approved' || phase.kind === 'refused' || phase.kind === 'gone'

  const review = useCallback(
    async (approve: boolean) => {
      // A refusal without a reason is refused here before the server has to:
      // the note is what makes the decision legible to whoever reads the
      // history next.
      if (!approve && note.trim().length === 0) {
        setNoteMissing(true)
        return
      }

      setNoteMissing(false)
      setPhase({ kind: 'submitting' })

      try {
        const { data, error: failure, response } = await api.POST(
          '/api/amendments/{amendmentRequestId}/review',
          {
            params: { path: { amendmentRequestId: row.amendmentRequestId } },
            body: { data: { approve, note: note.trim() || null } },
          },
        )

        if (response.ok) {
          setPhase(
            approve
              ? { kind: 'approved', result: (data?.data ?? null) as ReviewResponse }
              : { kind: 'refused' },
          )
          return
        }

        const ncbrs = toNcbrsError(failure, response.status)

        // Two 409s that must not read alike. "The record changed since
        // submission" leaves the correction standing; "already reviewed"
        // means someone else resolved it and it is gone from the queue.
        if (ncbrs.status === 409) {
          if (/changed since|changed after/i.test(ncbrs.title)) {
            setPhase({ kind: 'drift', error: ncbrs })
          } else {
            setPhase({ kind: 'gone' })
          }
          return
        }

        setPhase({ kind: 'error', error: ncbrs })
      } catch (cause) {
        setPhase({ kind: 'error', error: unreachableError(cause) })
      }
    },
    [api, note, row.amendmentRequestId],
  )

  const close = useCallback(() => {
    if (resolved) {
      onResolved(row.amendmentRequestId)
    } else {
      onClose()
    }
  }, [resolved, onResolved, onClose, row.amendmentRequestId])

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : close())}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Review this correction</DialogTitle>
          <DialogDescription>
            {row.childFullName} · <span className="font-mono">{row.brn}</span> · {row.facilityName}
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <div>
            <p className="text-muted-foreground mb-1 text-sm font-medium">Proposed change</p>
            <ChangeList changes={row.changes} />
          </div>

          <div>
            <p className="text-muted-foreground text-sm">
              <span className="font-medium text-foreground">Reason given:</span> {row.reason}
            </p>
            <p className="text-muted-foreground text-sm">
              Submitted by {row.submittedByRegistrarName}, {ageInWords(row.submittedAtUtc)}.
            </p>
          </div>

          {phase.kind === 'confirm' || phase.kind === 'submitting' || phase.kind === 'drift' || phase.kind === 'error' ? (
            <>
              {phase.kind === 'drift' ? (
                <Alert>
                  <TriangleAlert />
                  <AlertTitle>The record changed since this was submitted</AlertTitle>
                  <AlertDescription>
                    {phase.error.fields.map((item) => item.message).join(' ') ||
                      'This correction was composed against values the register no longer holds, so it was not applied.'}{' '}
                    The correction may still be right — but not against these values. Nothing has
                    changed. Close this and re-open the record to correct it against what it now
                    says.
                  </AlertDescription>
                </Alert>
              ) : null}

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

              {phase.kind !== 'drift' ? (
                <div className="grid gap-2">
                  <Label htmlFor="review-note">
                    Note{' '}
                    <span className="text-muted-foreground font-normal">
                      — required to refuse, optional to approve
                    </span>
                  </Label>
                  <Textarea
                    id="review-note"
                    value={note}
                    onChange={(event) => {
                      setNote(event.target.value)
                      if (noteMissing) {
                        setNoteMissing(false)
                      }
                    }}
                    placeholder="What was checked, and the decision made."
                    aria-invalid={noteMissing || undefined}
                    aria-describedby={noteMissing ? 'review-note-error' : undefined}
                    disabled={busy}
                  />
                  {noteMissing ? (
                    <p id="review-note-error" className="text-destructive text-sm">
                      A refusal needs a reason. It is kept as part of the record's history.
                    </p>
                  ) : null}
                </div>
              ) : null}
            </>
          ) : null}

          {phase.kind === 'approved' ? (
            <Alert>
              <AlertTitle>Correction approved</AlertTitle>
              <AlertDescription>
                The register now reads as corrected.
                {phase.result?.certificateInvalidated ? (
                  <span className="text-destructive mt-2 block">
                    A certificate had been issued for this record and covered a field just
                    changed. It has been withdrawn and added to the revocation list — the printed
                    document will now fail verification, and a corrected certificate must be
                    reissued.
                  </span>
                ) : null}
              </AlertDescription>
            </Alert>
          ) : null}

          {phase.kind === 'refused' ? (
            <Alert>
              <AlertTitle>Correction refused</AlertTitle>
              <AlertDescription>
                The record is unchanged. The refusal and its reason are kept as part of the
                record's history.
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
          {resolved || phase.kind === 'drift' ? (
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
                Approve
              </Button>
            </>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function LoadingQueue() {
  return (
    <Card aria-busy>
      <CardContent className="space-y-3 pt-6">
        <Skeleton className="h-4 w-48" />
        <Skeleton className="h-12 w-full" />
        <Skeleton className="h-12 w-full" />
        <Skeleton className="h-12 w-full" />
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
            : error.fields.map((item) => item.message).join(' ') ||
              'The approval queue could not be loaded.'}
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

/**
 * How long a correction has been waiting, in the coarse terms that matter for
 * a queue read top to bottom. The exact timestamp is on the row's title
 * attribute for anyone who needs it.
 */
function ageInWords(iso: string): string {
  const then = new Date(iso).getTime()
  if (Number.isNaN(then)) {
    return ''
  }

  const days = Math.floor((Date.now() - then) / 86_400_000)
  if (days <= 0) {
    return 'today'
  }
  if (days === 1) {
    return 'yesterday'
  }
  if (days < 14) {
    return `${days} days ago`
  }

  const weeks = Math.floor(days / 7)
  if (weeks < 9) {
    return `${weeks} weeks ago`
  }

  const months = Math.floor(days / 30)
  return `${months} months ago`
}

function plural(count: number): string {
  return `${count.toLocaleString()} ${count === 1 ? 'correction' : 'corrections'}`
}
