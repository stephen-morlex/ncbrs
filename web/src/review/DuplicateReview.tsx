import { useCallback, useEffect, useState } from 'react'
import { CircleAlert, Copy, TriangleAlert } from 'lucide-react'
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

type Candidate = components['schemas']['DuplicateCandidateResponse']
type Facility = components['schemas']['FacilityResponse']

const AllFacilities = 'all'

/**
 * Suspected cross-facility duplicates: the same birth registered twice,
 * usually once at a village post and again at a hospital, each issuing a BRN
 * from its own block. Exact-BRN checks cannot catch these — the numbers differ
 * on purpose — so a matcher flags them for a human, and this is that human's
 * queue, most-likely first.
 *
 * **This is never an automatic call.** Matching names and dates produces false
 * positives with certainty, and the cost of a wrong automatic decision is a
 * real child left without a legal identity. So the reviewer sees both records
 * and what lined up, and decides.
 *
 * **Confirming supersedes an identity.** The two records are recorded as one
 * child: the earlier registration — closest to the birth, and the one a family
 * most likely already holds a certificate for — is kept, and the other is
 * superseded, with any certificate on it withdrawn. Nothing is deleted.
 * Because that is a heavier act than an amendment (it withdraws a whole second
 * legal identity, not a stale document), it sits with oversight roles, never
 * the facility staff who filed the records.
 */
export function DuplicateReview() {
  const api = useApiClient()

  const [rows, setRows] = useState<Candidate[] | null>(null)
  const [total, setTotal] = useState(0)
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)

  const [facilities, setFacilities] = useState<Facility[]>([])
  const [facilityId, setFacilityId] = useState<string>(AllFacilities)

  const [reviewing, setReviewing] = useState<Candidate | null>(null)

  const load = useCallback(
    async (after: string | null, facility: string) => {
      if (after) {
        setLoadingMore(true)
      } else {
        setLoading(true)
      }
      setError(null)

      try {
        const { data, error: failure, response } = await api.GET('/api/duplicates/pending', {
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

  const removeRow = useCallback((duplicateCandidateId: string) => {
    setRows((current) => current?.filter((row) => row.duplicateCandidateId !== duplicateCandidateId) ?? null)
    setTotal((current) => Math.max(0, current - 1))
  }, [])

  return (
    <div className="mx-auto w-full max-w-5xl space-y-6">
      <PageHeader
        title="Duplicate review"
        description="Records that may be the same child registered twice across facilities, waiting on a decision."
      />

      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="grid gap-2">
          <Label htmlFor="duplicate-facility-filter">Facility</Label>
          <Select value={facilityId} onValueChange={setFacilityId}>
            <SelectTrigger id="duplicate-facility-filter" className="w-64">
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
          fallback="The duplicate queue could not be loaded."
        />
      ) : null}

      {rows !== null && !error ? (
        rows.length === 0 ? (
          <Empty className="border">
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <Copy />
              </EmptyMedia>
              <EmptyTitle>No suspected duplicates</EmptyTitle>
              <EmptyDescription>
                {facilityId === AllFacilities
                  ? 'The matcher has flagged nothing waiting on a decision in your district.'
                  : 'This facility has none waiting. Clear the filter to see the rest of the district.'}
              </EmptyDescription>
            </EmptyHeader>
          </Empty>
        ) : (
          <Card>
            <CardContent className="pt-6">
              <p className="text-muted-foreground mb-3 text-sm">
                {plural(total)} waiting, most likely first.
              </p>

              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>This record</TableHead>
                    <TableHead>Possible match</TableHead>
                    <TableHead>Why flagged</TableHead>
                    <TableHead className="text-right">Match</TableHead>
                    <TableHead>Flagged</TableHead>
                    <TableHead className="text-right">Review</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {rows.map((row) => (
                    <TableRow key={row.duplicateCandidateId}>
                      <TableCell className="align-top">
                        <div className="font-medium">{row.childFullName}</div>
                        <div className="text-muted-foreground font-mono text-xs">{row.brn}</div>
                      </TableCell>
                      <TableCell className="align-top">
                        <div className="font-medium">{row.matchedChildFullName}</div>
                        <div className="text-muted-foreground font-mono text-xs">{row.matchedBrn}</div>
                      </TableCell>
                      <TableCell className="text-muted-foreground max-w-64 align-top text-sm">
                        {row.reasons}
                      </TableCell>
                      <TableCell className="text-right align-top">
                        <ScoreBadge score={row.score} />
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
        <ReviewDuplicateDialog
          row={reviewing}
          onClose={() => setReviewing(null)}
          onResolved={(duplicateCandidateId) => {
            removeRow(duplicateCandidateId)
            setReviewing(null)
          }}
        />
      ) : null}
    </div>
  )
}

/**
 * The matcher's 0–100 confidence. Shown with the record, not instead of it: a
 * high score is a reason to look, never the decision — the reviewer makes
 * that from the records themselves.
 */
function ScoreBadge({ score }: { score: number }) {
  const variant = score >= 80 ? 'destructive' : score >= 50 ? 'default' : 'secondary'
  return <Badge variant={variant}>{score} / 100</Badge>
}

type Phase =
  | { kind: 'confirm' }
  | { kind: 'submitting' }
  | { kind: 'duplicate' }
  | { kind: 'distinct' }
  | { kind: 'gone' }
  | { kind: 'error'; error: NcbrsError }

/**
 * Adjudicate one suspected duplicate.
 *
 * The two records are shown side by side with what the matcher saw. The
 * reviewer decides they are **the same birth** — which supersedes the later
 * registration and withdraws any certificate on it — or **different births**,
 * which dismisses the flag and leaves both standing.
 *
 * A note is required either way. This is a judgement about a citizen's legal
 * identity, kept as part of the record's history, and the confirm path in
 * particular withdraws a whole second identity — not a decision that should
 * ever be unexplained.
 */
function ReviewDuplicateDialog({
  row,
  onClose,
  onResolved,
}: {
  row: Candidate
  onClose: () => void
  onResolved: (duplicateCandidateId: string) => void
}) {
  const api = useApiClient()

  const [note, setNote] = useState('')
  const [phase, setPhase] = useState<Phase>({ kind: 'confirm' })
  const [noteMissing, setNoteMissing] = useState(false)

  const busy = phase.kind === 'submitting'
  const resolved = phase.kind === 'duplicate' || phase.kind === 'distinct' || phase.kind === 'gone'

  const review = useCallback(
    async (isDuplicate: boolean) => {
      if (note.trim().length === 0) {
        setNoteMissing(true)
        return
      }

      setNoteMissing(false)
      setPhase({ kind: 'submitting' })

      try {
        const { error: failure, response } = await api.POST(
          '/api/duplicates/{duplicateCandidateId}/review',
          {
            params: { path: { duplicateCandidateId: row.duplicateCandidateId } },
            body: { data: { isDuplicate, note: note.trim() } },
          },
        )

        if (response.ok) {
          setPhase(isDuplicate ? { kind: 'duplicate' } : { kind: 'distinct' })
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
    [api, note, row.duplicateCandidateId],
  )

  const close = useCallback(() => {
    if (resolved) {
      onResolved(row.duplicateCandidateId)
    } else {
      onClose()
    }
  }, [resolved, onResolved, onClose, row.duplicateCandidateId])

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : close())}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Are these the same child?</DialogTitle>
          <DialogDescription>
            The matcher scored this {row.score} / 100. The decision is yours, from the records.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <div className="grid grid-cols-2 gap-3">
            <RecordCard label="This record" name={row.childFullName} brn={row.brn} />
            <RecordCard label="Possible match" name={row.matchedChildFullName} brn={row.matchedBrn} />
          </div>

          <div>
            <p className="text-muted-foreground text-xs">Why the matcher flagged it</p>
            <p className="text-sm">{row.reasons}</p>
          </div>

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
                If these are one child, the earlier registration is kept and the other is
                superseded — any certificate on it is withdrawn. Nothing is deleted.
              </p>

              <div className="grid gap-2">
                <Label htmlFor="duplicate-note">
                  Note{' '}
                  <span className="text-muted-foreground font-normal">
                    — what you checked, and the decision (required)
                  </span>
                </Label>
                <Textarea
                  id="duplicate-note"
                  value={note}
                  onChange={(event) => {
                    setNote(event.target.value)
                    if (noteMissing) {
                      setNoteMissing(false)
                    }
                  }}
                  placeholder="e.g. Same mother, same date and place of birth; the hospital record repeats the post's."
                  aria-invalid={noteMissing || undefined}
                  aria-describedby={noteMissing ? 'duplicate-note-error' : undefined}
                  disabled={busy}
                />
                {noteMissing ? (
                  <p id="duplicate-note-error" className="text-destructive text-sm">
                    A reason is required. This is a judgement about a legal identity.
                  </p>
                ) : null}
              </div>
            </>
          ) : null}

          {phase.kind === 'duplicate' ? (
            <Alert>
              <AlertTitle>Recorded as one child</AlertTitle>
              <AlertDescription>
                The earlier registration is kept; the other is superseded and any certificate on it
                withdrawn. Both records remain in the register, linked. Your note is kept as part of
                the history.
              </AlertDescription>
            </Alert>
          ) : null}

          {phase.kind === 'distinct' ? (
            <Alert>
              <AlertTitle>Recorded as different births</AlertTitle>
              <AlertDescription>
                Both registrations stand. The flag is cleared and your note is kept, so the same
                pair is not raised again as though it had never been looked at.
              </AlertDescription>
            </Alert>
          ) : null}

          {phase.kind === 'gone' ? (
            <Alert>
              <TriangleAlert />
              <AlertTitle>Already reviewed</AlertTitle>
              <AlertDescription>
                Another reviewer has already decided this pair. It has left the queue.
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
                Different births
              </Button>
              <Button
                variant="destructive"
                onClick={() => void review(true)}
                disabled={busy}
              >
                {busy ? <Spinner /> : null}
                Same birth
              </Button>
            </>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function RecordCard({ label, name, brn }: { label: string; name: string; brn: string }) {
  return (
    <div className="rounded-md border p-3">
      <p className="text-muted-foreground text-xs">{label}</p>
      <p className="font-medium">{name}</p>
      <p className="text-muted-foreground font-mono text-xs">{brn}</p>
    </div>
  )
}

function plural(count: number): string {
  return `${count.toLocaleString()} ${count === 1 ? 'pair' : 'pairs'}`
}
