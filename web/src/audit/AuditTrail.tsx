import { type FormEvent, useCallback, useEffect, useState } from 'react'
import { useSearchParams } from 'react-router'
import { CircleAlert, ScrollText, Search, TriangleAlert } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from '@/components/ui/empty'
import { Input } from '@/components/ui/input'
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
import type { components } from '@/api/generated/api'
import { type NcbrsError, messagesFor, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'
import { PageHeader } from '@/shell/PageHeader'

type AuditEntry = components['schemas']['AuditEntryResponse']

const BrnField = 'brn'
const FromField = 'from'
const ToField = 'to'

/**
 * Reading the audit trail.
 *
 * Until this screen the trail was reachable only through the API, and before
 * that only by SQL — which for something whose entire purpose is
 * accountability is the wrong shape. A control that needs a database client
 * to consult works only after a dispute has already escalated to whoever
 * holds the credentials.
 *
 * **The screen says that reading is recorded.** Not as a warning to scare
 * anyone off: a district officer checking a disputed record is doing their
 * job. It is there because the same read looks identical whether it is that
 * or someone checking what a colleague searched for, and the person doing
 * the first should know the second is not invisible either.
 */
export function AuditTrail() {
  const api = useApiClient()

  // The BRN can arrive in the URL, so a record page can link straight to its
  // own history and a colleague can be sent that link.
  const [params] = useSearchParams()

  const [form, setForm] = useState({ brn: params.get('brn') ?? '', from: '', to: '' })
  const [entries, setEntries] = useState<AuditEntry[] | null>(null)
  const [total, setTotal] = useState(0)
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(false)

  const load = useCallback(
    async (query: { brn: string; from: string; to: string }, after: string | null) => {
      setLoading(true)
      setError(null)

      try {
        const { data, error: failure, response } = await api.GET('/api/audit', {
          params: {
            query: {
              ...(query.brn.trim() ? { brn: query.brn.trim() } : {}),
              ...(query.from ? { from: query.from } : {}),
              ...(query.to ? { to: query.to } : {}),
              ...(after ? { after } : {}),
            },
          },
        })

        if (failure || !response.ok) {
          setError(toNcbrsError(failure, response.status))
          return
        }

        const page = data?.data

        setEntries((previous) =>
          after ? [...(previous ?? []), ...(page?.items ?? [])] : (page?.items ?? []),
        )
        setTotal(page?.total ?? 0)
        setNextCursor(page?.nextCursor ?? null)
      } catch (cause) {
        setError(unreachableError(cause))
      } finally {
        setLoading(false)
      }
    },
    [api],
  )

  // Opened with a BRN in the URL: show that record's history without making
  // the reader press a button they did not ask for.
  const linkedBrn = params.get('brn')

  useEffect(() => {
    if (linkedBrn) {
      void load({ brn: linkedBrn, from: '', to: '' }, null)
    }
  }, [linkedBrn, load])

  function onSubmit(event: FormEvent) {
    event.preventDefault()
    setEntries(null)
    setNextCursor(null)
    void load(form, null)
  }

  const brnMessages = messagesFor(error, BrnField)
  const toMessages = messagesFor(error, ToField)

  return (
    <div className="mx-auto w-full max-w-5xl space-y-6">
      <PageHeader
        title="Audit trail"
        description="Every act recorded against the register, newest first."
      />

      <Card>
        <CardContent className="pt-6">
          <form onSubmit={onSubmit} className="grid gap-4">
            <div className="grid gap-2">
              <Label htmlFor={BrnField}>Registration number</Label>
              <Input
                id={BrnField}
                value={form.brn}
                onChange={(event) => setForm({ ...form, brn: event.target.value })}
                placeholder="Leave blank for everything in your county"
                autoComplete="off"
                aria-invalid={brnMessages.length > 0 || undefined}
                aria-describedby={brnMessages.length > 0 ? `${BrnField}-error` : undefined}
              />
              {brnMessages.length > 0 ? (
                <p id={`${BrnField}-error`} className="text-destructive text-sm">
                  {brnMessages.join(' ')}
                </p>
              ) : null}
            </div>

            <div className="grid gap-4 sm:grid-cols-2">
              <div className="grid gap-2">
                <Label htmlFor={FromField}>From</Label>
                <Input
                  id={FromField}
                  type="date"
                  value={form.from}
                  onChange={(event) => setForm({ ...form, from: event.target.value })}
                />
              </div>
              <div className="grid gap-2">
                <Label htmlFor={ToField}>To</Label>
                <Input
                  id={ToField}
                  type="date"
                  value={form.to}
                  onChange={(event) => setForm({ ...form, to: event.target.value })}
                  aria-invalid={toMessages.length > 0 || undefined}
                  aria-describedby={toMessages.length > 0 ? `${ToField}-error` : undefined}
                />
                {toMessages.length > 0 ? (
                  <p id={`${ToField}-error`} className="text-destructive text-sm">
                    {toMessages.join(' ')}
                  </p>
                ) : null}
              </div>
            </div>

            <div className="flex flex-wrap items-center gap-3">
              <Button type="submit" disabled={loading}>
                {loading && entries === null ? <Spinner /> : <Search />}
                Show the trail
              </Button>
              {/* Said plainly and without alarm. A district officer checking a
                  disputed record is doing their job; the point is that the
                  same read looks identical whether it is that or someone
                  checking up on a colleague, and neither is invisible. */}
              <p className="text-muted-foreground text-sm">
                Opening the trail is itself recorded in it.
              </p>
            </div>
          </form>
        </CardContent>
      </Card>

      {loading && entries === null ? <LoadingTrail /> : null}

      {error && brnMessages.length === 0 && toMessages.length === 0 ? (
        <Failure error={error} />
      ) : null}

      {entries !== null && !error ? (
        <Entries
          entries={entries}
          total={total}
          onMore={nextCursor ? () => void load(form, nextCursor) : null}
          loadingMore={loading}
        />
      ) : null}
    </div>
  )
}

function Entries({
  entries,
  total,
  onMore,
  loadingMore,
}: {
  entries: AuditEntry[]
  total: number
  onMore: (() => void) | null
  loadingMore: boolean
}) {
  if (entries.length === 0) {
    return (
      <Empty className="border">
        <EmptyHeader>
          <EmptyMedia variant="icon">
            <ScrollText />
          </EmptyMedia>
          <EmptyTitle>Nothing recorded for that</EmptyTitle>
          <EmptyDescription>
            An empty trail for a record that exists means nothing has happened to it since it
            was registered — not that its history is missing.
          </EmptyDescription>
        </EmptyHeader>
      </Empty>
    )
  }

  return (
    <Card>
      <CardContent className="pt-6">
        <p className="text-muted-foreground mb-3 text-sm">
          Showing {entries.length} of {total}
        </p>

        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>When</TableHead>
              <TableHead>Act</TableHead>
              <TableHead>Subject</TableHead>
              <TableHead>Who</TableHead>
              <TableHead>Where from</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {entries.map((entry) => (
              <TableRow key={entry.auditLogId}>
                <TableCell className="whitespace-nowrap font-mono text-xs">
                  {formatInstant(entry.timestampUtc)}
                </TableCell>
                <TableCell className="font-medium">{entry.action}</TableCell>
                <TableCell>
                  <span className="font-mono text-xs">{entry.entityId}</span>
                  <span className="text-muted-foreground block text-xs">{entry.entityType}</span>
                </TableCell>
                <TableCell>
                  {/* A name, or an honest statement that no person did this.
                      A sweep raising a device alert has no actor, and
                      inventing one would misdescribe the act. */}
                  {entry.actorName ?? (
                    <span className="text-muted-foreground italic">no person</span>
                  )}
                </TableCell>
                <TableCell>
                  <Badge variant="outline" className="font-mono text-xs">
                    {entry.deviceId}
                  </Badge>
                  {/* Empty means the row predates the district column and can
                      never be given one. Said explicitly, because a blank
                      cell reads as a bug. */}
                  {entry.countyCode ? null : (
                    <span className="text-muted-foreground block text-xs">
                      county not recorded
                    </span>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>

        {onMore ? (
          <div className="mt-4 flex justify-center">
            <Button variant="outline" onClick={onMore} disabled={loadingMore}>
              {loadingMore ? <Spinner /> : null}
              Show more
            </Button>
          </div>
        ) : null}
      </CardContent>
    </Card>
  )
}

function LoadingTrail() {
  return (
    <Card aria-busy>
      <CardContent className="space-y-3 pt-6">
        <Skeleton className="h-4 w-40" />
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-10 w-full" />
      </CardContent>
    </Card>
  )
}

function Failure({ error }: { error: NcbrsError }) {
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
            : error.fields.map((item) => item.message).join(' ')}
        </EmptyDescription>
      </EmptyHeader>
      {error.status ? (
        <EmptyContent>
          <Badge variant="outline">HTTP {error.status}</Badge>
        </EmptyContent>
      ) : null}
    </Empty>
  )
}

/**
 * An audit timestamp *is* an instant, unlike a date of birth, so it is shown
 * with its time. Rendered in UTC as stored: a trail compared across two
 * offices in different offsets must read the same in both, and "who acted
 * first" is a question this table exists to answer.
 */
function formatInstant(value: string): string {
  return value.replace('T', ' ').replace(/\.\d+/, '').replace('Z', ' UTC')
}
