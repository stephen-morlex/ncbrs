import { useCallback, useEffect, useState } from 'react'
import { CircleAlert, ShieldCheck, TriangleAlert } from 'lucide-react'
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
import { formatDate } from '@/records/RecordDetail'
import { ageInWords, QueueFailure, QueueSkeleton } from './queueParts'

type Conflict = components['schemas']['AmendmentConflictResponse']
type Facility = components['schemas']['FacilityResponse']

const AllFacilities = 'all'

/**
 * Corrections that arrived composed against a value the register no longer
 * held (draft 6.3), waiting on a registrar's judgement.
 *
 * **This is a different question from the approval queue.** Approval asks
 * whether a change should be made. This asks about a change that has already
 * been resolved by last-writer-wins, where the wrong value may have won — so
 * the age here measures how long a possibly-wrong value has been standing on a
 * legal record.
 *
 * **Per field, not per record.** A device correcting a birth weight while the
 * centre corrected a name has clashed with nothing; only the fields that
 * actually disagree appear, one row each.
 *
 * **There is no way to change the value from here** — deliberately. A reviewer
 * either upholds the resolution or records that they have corrected it by a
 * separate amendment. Restoring what the register previously held is an
 * ordinary amendment, made from the record, so that one code path stays
 * responsible for previous values, approval rules and certificate withdrawal.
 */
export function AmendmentConflicts() {
  const api = useApiClient()

  const [rows, setRows] = useState<Conflict[] | null>(null)
  const [total, setTotal] = useState(0)
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)

  const [facilities, setFacilities] = useState<Facility[]>([])
  const [facilityId, setFacilityId] = useState<string>(AllFacilities)

  const [reviewing, setReviewing] = useState<Conflict | null>(null)

  const load = useCallback(
    async (after: string | null, facility: string) => {
      if (after) {
        setLoadingMore(true)
      } else {
        setLoading(true)
      }
      setError(null)

      try {
        const { data, error: failure, response } = await api.GET('/api/amendments/conflicts', {
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

  const removeRow = useCallback((amendmentConflictId: string) => {
    setRows((current) => current?.filter((row) => row.amendmentConflictId !== amendmentConflictId) ?? null)
    setTotal((current) => Math.max(0, current - 1))
  }, [])

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="grid gap-2">
          <Label htmlFor="conflict-facility-filter">Facility</Label>
          <Select value={facilityId} onValueChange={setFacilityId}>
            <SelectTrigger id="conflict-facility-filter" className="w-64">
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
          fallback="The conflicts queue could not be loaded."
        />
      ) : null}

      {rows !== null && !error ? (
        rows.length === 0 ? (
          <Empty className="border">
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <ShieldCheck />
              </EmptyMedia>
              <EmptyTitle>No conflicts to review</EmptyTitle>
              <EmptyDescription>
                {facilityId === AllFacilities
                  ? 'No correction has arrived disagreeing with what the register held. This is the queue you want to be empty.'
                  : 'This facility has no conflicts waiting. Clear the filter to see the rest of the district.'}
              </EmptyDescription>
            </EmptyHeader>
          </Empty>
        ) : (
          <Card>
            <CardContent className="pt-6">
              <p className="text-muted-foreground mb-3 text-sm">
                {plural(total)} flagged, oldest first.
              </p>

              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Record</TableHead>
                    <TableHead>Field</TableHead>
                    <TableHead>Device saw</TableHead>
                    <TableHead>Register held</TableHead>
                    <TableHead>Standing value</TableHead>
                    <TableHead>Flagged</TableHead>
                    <TableHead className="text-right">Review</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {rows.map((row) => (
                    <TableRow key={row.amendmentConflictId}>
                      <TableCell className="align-top">
                        <div className="font-medium">{row.childFullName}</div>
                        <div className="text-muted-foreground font-mono text-xs">{row.brn}</div>
                      </TableCell>
                      <TableCell className="align-top font-medium">{row.field}</TableCell>
                      {/* What the offline device believed the field said when it
                          composed the correction. */}
                      <TableCell className="text-muted-foreground align-top">
                        {row.expectedPreviousValue ?? '—'}
                      </TableCell>
                      {/* What the register actually held when the change arrived —
                          the disagreement itself. */}
                      <TableCell className="align-top">{row.actualPreviousValue ?? '—'}</TableCell>
                      <TableCell className="align-top">
                        <StandingValue value={row.resolvedValue} />
                      </TableCell>
                      <TableCell
                        className="text-muted-foreground align-top whitespace-nowrap"
                        title={formatDate(row.detectedAtUtc)}
                      >
                        {ageInWords(row.detectedAtUtc)}
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
        <ReviewConflictDialog
          row={reviewing}
          onClose={() => setReviewing(null)}
          onResolved={(amendmentConflictId) => {
            removeRow(amendmentConflictId)
            setReviewing(null)
          }}
        />
      ) : null}
    </div>
  )
}

/**
 * The value standing on the record under last-writer-wins — or, on the
 * approval track, the fact that nothing has been applied at all. The API
 * returns null for the second case on purpose: naming a winner where none has
 * been chosen would misdescribe the record.
 */
function StandingValue({ value }: { value: string | null }) {
  if (value === null) {
    return (
      <span className="text-muted-foreground text-sm italic">not applied — awaiting approval</span>
    )
  }

  return <span>{value}</span>
}

type Phase =
  | { kind: 'confirm' }
  | { kind: 'submitting' }
  | { kind: 'reviewed'; upheld: boolean }
  | { kind: 'gone' }
  | { kind: 'error'; error: NcbrsError }

/**
 * Review one flagged conflict.
 *
 * The reviewer has exactly two judgements to record, and neither changes a
 * value: **uphold** says the value now standing is correct and the resolution
 * stands; **corrected** says they have already put it right by a separate
 * amendment. Restoring the previous value is that separate amendment, made
 * from the record — not something this dialog can do, by design.
 *
 * A note is required either way: it is the record of what was checked and why
 * the outcome is right, and a conflict resolved without one tells the next
 * person nothing.
 */
function ReviewConflictDialog({
  row,
  onClose,
  onResolved,
}: {
  row: Conflict
  onClose: () => void
  onResolved: (amendmentConflictId: string) => void
}) {
  const api = useApiClient()

  const [note, setNote] = useState('')
  const [phase, setPhase] = useState<Phase>({ kind: 'confirm' })
  const [noteMissing, setNoteMissing] = useState(false)

  const busy = phase.kind === 'submitting'
  const resolved = phase.kind === 'reviewed' || phase.kind === 'gone'

  const review = useCallback(
    async (uphold: boolean) => {
      // The server requires the note; refusing here first keeps the decision
      // legible to whoever reads the record next.
      if (note.trim().length === 0) {
        setNoteMissing(true)
        return
      }

      setNoteMissing(false)
      setPhase({ kind: 'submitting' })

      try {
        const { error: failure, response } = await api.POST(
          '/api/amendments/conflicts/{amendmentConflictId}/review',
          {
            params: { path: { amendmentConflictId: row.amendmentConflictId } },
            body: { data: { uphold, note: note.trim() } },
          },
        )

        if (response.ok) {
          setPhase({ kind: 'reviewed', upheld: uphold })
          return
        }

        const ncbrs = toNcbrsError(failure, response.status)

        // Already resolved by someone else, or no longer present — either way
        // it has left the queue.
        if (ncbrs.status === 409 || ncbrs.status === 404) {
          setPhase({ kind: 'gone' })
          return
        }

        setPhase({ kind: 'error', error: ncbrs })
      } catch (cause) {
        setPhase({ kind: 'error', error: unreachableError(cause) })
      }
    },
    [api, note, row.amendmentConflictId],
  )

  const close = useCallback(() => {
    if (resolved) {
      onResolved(row.amendmentConflictId)
    } else {
      onClose()
    }
  }, [resolved, onResolved, onClose, row.amendmentConflictId])

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : close())}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Review this conflict</DialogTitle>
          <DialogDescription>
            {row.childFullName} · <span className="font-mono">{row.brn}</span> · {row.field}
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          {/* Stacked on a phone: three columns of values at 375px wraps each
              one to a word per line, and this is the comparison a registrar
              has to read carefully to rule on a conflict. */}
          <div className="grid grid-cols-1 gap-3 text-sm sm:grid-cols-3">
            <div>
              <p className="text-muted-foreground text-xs">Device saw</p>
              <p>{row.expectedPreviousValue ?? '—'}</p>
            </div>
            <div>
              <p className="text-muted-foreground text-xs">Register held</p>
              <p>{row.actualPreviousValue ?? '—'}</p>
            </div>
            <div>
              <p className="text-muted-foreground text-xs">Standing value</p>
              <StandingValue value={row.resolvedValue} />
            </div>
          </div>

          <p className="text-muted-foreground text-sm">
            Submitted by {row.submittedByRegistrarName}, flagged {ageInWords(row.detectedAtUtc)}.
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

              <p className="text-muted-foreground text-sm">
                This does not change the value. To restore what the register previously held, make
                an ordinary correction from the record.
              </p>

              <div className="grid gap-2">
                <Label htmlFor="conflict-note">
                  Note{' '}
                  <span className="text-muted-foreground font-normal">
                    — what you checked, and why the outcome is right
                  </span>
                </Label>
                <Textarea
                  id="conflict-note"
                  value={note}
                  onChange={(event) => {
                    setNote(event.target.value)
                    if (noteMissing) {
                      setNoteMissing(false)
                    }
                  }}
                  placeholder="e.g. Confirmed against the birth notification; the register's value is correct."
                  aria-invalid={noteMissing || undefined}
                  aria-describedby={noteMissing ? 'conflict-note-error' : undefined}
                  disabled={busy}
                />
                {noteMissing ? (
                  <p id="conflict-note-error" className="text-destructive text-sm">
                    A reason is required. It is kept as part of the record's history.
                  </p>
                ) : null}
              </div>
            </>
          ) : null}

          {phase.kind === 'reviewed' ? (
            <Alert>
              <AlertTitle>{phase.upheld ? 'Resolution upheld' : 'Marked as corrected'}</AlertTitle>
              <AlertDescription>
                {phase.upheld
                  ? 'The value standing on the record is confirmed correct. Your note is kept as part of its history.'
                  : 'Recorded that you corrected this by a separate amendment. Your note is kept as part of the record’s history.'}
              </AlertDescription>
            </Alert>
          ) : null}

          {phase.kind === 'gone' ? (
            <Alert>
              <TriangleAlert />
              <AlertTitle>Already reviewed</AlertTitle>
              <AlertDescription>
                Another registrar has already judged this conflict. It has left the queue.
              </AlertDescription>
            </Alert>
          ) : null}
        </div>

        <DialogFooter>
          {resolved ? (
            <Button onClick={close}>Done</Button>
          ) : (
            <>
              <Button variant="outline" onClick={() => void review(false)} disabled={busy}>
                Mark corrected
              </Button>
              <Button onClick={() => void review(true)} disabled={busy}>
                {busy ? <Spinner /> : null}
                Uphold
              </Button>
            </>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function plural(count: number): string {
  return `${count.toLocaleString()} ${count === 1 ? 'conflict' : 'conflicts'}`
}
