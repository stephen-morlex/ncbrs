import { type FormEvent, useState } from 'react'
import { Link } from 'react-router'
import { CircleAlert, Search, ShieldAlert, TriangleAlert, Users } from 'lucide-react'
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

type SearchHit = components['schemas']['BirthRecordSearchHit']

const NameField = 'name'
const FromField = 'bornFrom'
const ToField = 'bornTo'
const ShownFields = [NameField, FromField, ToField]

/**
 * Searching the register by name and date.
 *
 * The server decides what may be searched and records that it was; this only
 * asks. Two consequences shape the screen:
 *
 * - **A refusal is explained, not swallowed.** The API refuses a search of
 *   another district outright rather than narrowing it, precisely so the
 *   caller is not told "no such child" about a district they were never
 *   allowed to ask about. Rendering that 403 as an empty result would undo
 *   the whole point of the refusal.
 * - **The scope is stated before anyone searches.** A registrar who does not
 *   know their results stop at the district boundary will read an empty page
 *   as "this child is not registered", which for a family who moved is
 *   exactly wrong.
 */
export function RecordSearch() {
  const api = useApiClient()

  const [form, setForm] = useState({ name: '', bornFrom: '', bornTo: '' })
  const [results, setResults] = useState<SearchHit[] | null>(null)
  const [total, setTotal] = useState(0)
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [searching, setSearching] = useState(false)

  async function run(after: string | null) {
    setSearching(true)
    setError(null)

    try {
      const { data, error: failure, response } = await api.GET('/api/birthrecords/search', {
        params: {
          query: {
            // Blank strings are omitted rather than sent: an empty `name=`
            // is not the same request as no name at all, and the server
            // counts criteria to decide whether this is a search or a bulk
            // read of a district.
            ...(form.name.trim() ? { name: form.name.trim() } : {}),
            ...(form.bornFrom ? { bornFrom: form.bornFrom } : {}),
            ...(form.bornTo ? { bornTo: form.bornTo } : {}),
            ...(after ? { after } : {}),
          },
        },
      })

      if (failure || !response.ok) {
        setError(toNcbrsError(failure, response.status))
        return
      }

      const page = data?.data

      // Appended, not replaced: "more" continues a list the registrar is
      // reading down, and replacing it would lose the rows they were
      // comparing.
      setResults((previous) => (after ? [...(previous ?? []), ...(page?.items ?? [])] : page?.items ?? []))
      setTotal(page?.total ?? 0)
      setNextCursor(page?.nextCursor ?? null)
    } catch (cause) {
      setError(unreachableError(cause))
    } finally {
      setSearching(false)
    }
  }

  function onSubmit(event: FormEvent) {
    event.preventDefault()
    setResults(null)
    setNextCursor(null)
    void run(null)
  }

  const nameMessages = messagesFor(error, NameField)
  const toMessages = messagesFor(error, ToField)

  return (
    <div className="mx-auto w-full max-w-4xl space-y-6">
      <PageHeader
        title="Search the register"
        description="By the child's name, a date-of-birth range, or both."
      />

      <Card>
        <CardContent className="pt-6">
          <form onSubmit={onSubmit} className="grid gap-4">
            <div className="grid gap-2">
              <Label htmlFor={NameField}>Child's name</Label>
              <Input
                id={NameField}
                value={form.name}
                onChange={(event) => setForm({ ...form, name: event.target.value })}
                placeholder="Any part of the name"
                autoComplete="off"
                aria-invalid={nameMessages.length > 0 || undefined}
                aria-describedby={nameMessages.length > 0 ? `${NameField}-error` : undefined}
              />
              {nameMessages.length > 0 ? (
                <p id={`${NameField}-error`} className="text-destructive text-sm">
                  {nameMessages.join(' ')}
                </p>
              ) : null}
            </div>

            <div className="grid gap-4 sm:grid-cols-2">
              <div className="grid gap-2">
                <Label htmlFor={FromField}>Born on or after</Label>
                <Input
                  id={FromField}
                  type="date"
                  value={form.bornFrom}
                  onChange={(event) => setForm({ ...form, bornFrom: event.target.value })}
                />
              </div>
              <div className="grid gap-2">
                <Label htmlFor={ToField}>Born on or before</Label>
                <Input
                  id={ToField}
                  type="date"
                  value={form.bornTo}
                  onChange={(event) => setForm({ ...form, bornTo: event.target.value })}
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
              <Button type="submit" disabled={searching}>
                {searching && results === null ? <Spinner /> : <Search />}
                Search
              </Button>
              {/* Said before the first search, not after a disappointing one.
                  A registrar who does not know the boundary exists reads an
                  empty page as "not registered". */}
              <p className="text-muted-foreground text-sm">
                Results are limited to your district, and every search is recorded.
              </p>
            </div>
          </form>
        </CardContent>
      </Card>

      {searching && results === null ? <LoadingResults /> : null}

      {error && nameMessages.length === 0 && toMessages.length === 0 ? (
        <Failure error={error} />
      ) : null}

      {results !== null && !error ? (
        <Results
          hits={results}
          total={total}
          onMore={nextCursor ? () => void run(nextCursor) : null}
          loadingMore={searching}
        />
      ) : null}
    </div>
  )
}

function Results({
  hits,
  total,
  onMore,
  loadingMore,
}: {
  hits: SearchHit[]
  total: number
  onMore: (() => void) | null
  loadingMore: boolean
}) {
  if (hits.length === 0) {
    return (
      <Empty className="border">
        <EmptyHeader>
          <EmptyMedia variant="icon">
            <Users />
          </EmptyMedia>
          <EmptyTitle>No matching records in your district</EmptyTitle>
          <EmptyDescription>
            A child registered in another district will not appear here. If the family moved,
            the Ministry can search nationally.
          </EmptyDescription>
        </EmptyHeader>
      </Empty>
    )
  }

  return (
    <Card>
      <CardContent className="pt-6">
        <div className="mb-3 flex items-center justify-between">
          <p className="text-muted-foreground text-sm">
            {/* "of N in your district", never a bare N. The total is scoped,
                and presenting it as if it were national would misstate what
                was searched. */}
            Showing {hits.length} of {total} in your district
          </p>
        </div>

        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Name</TableHead>
              <TableHead>Date of birth</TableHead>
              <TableHead>Registration number</TableHead>
              <TableHead>Facility</TableHead>
              <TableHead>Status</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {hits.map((hit) => (
              <TableRow key={hit.brn}>
                <TableCell className="font-medium">{hit.childFullName}</TableCell>
                <TableCell>{formatDate(hit.dateOfBirth)}</TableCell>
                <TableCell>
                  <Link to={`/records?brn=${encodeURIComponent(hit.brn)}`} className="font-mono underline underline-offset-4">
                    {hit.brn}
                  </Link>
                  {/* Kept visible after a real BRN is assigned: a family may
                      still be holding the slip the device printed. */}
                  {hit.provisionalIdentifier ? (
                    <span className="text-muted-foreground block font-mono text-xs">
                      {hit.provisionalIdentifier}
                    </span>
                  ) : null}
                </TableCell>
                <TableCell>{hit.facilityName}</TableCell>
                <TableCell>
                  <Badge variant={hit.status === 'Annulled' ? 'destructive' : 'secondary'}>
                    {hit.status}
                  </Badge>
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

function LoadingResults() {
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

function Failure({ error }: { error: NcbrsError }) {
  // A 403 here is the district boundary, not a broken session, and saying so
  // is the difference between a registrar referring the family to the
  // Ministry and concluding the child was never registered.
  const isScope = error.status === 403

  return (
    <Empty className="border">
      <EmptyHeader>
        <EmptyMedia variant="icon">
          {isScope ? <ShieldAlert /> : error.unreachable ? <TriangleAlert /> : <CircleAlert />}
        </EmptyMedia>
        <EmptyTitle>{error.title}</EmptyTitle>
        <EmptyDescription>
          {error.unreachable
            ? 'The registry did not answer. Check the connection before trying again.'
            : unattached(error).join(' ')}
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

/** Messages the form did not already attach to one of its own fields. */
function unattached(error: NcbrsError): string[] {
  const shown = new Set(ShownFields)

  return error.fields
    .filter((item) => !shown.has(item.field))
    .map((item) => item.message)
}

/**
 * A date of birth is a calendar date, not an instant. Rendering it in the
 * viewer's timezone would shift it a day either way depending on where they
 * are sitting, and a date of birth decides school entry and age of majority.
 */
function formatDate(value: string | undefined): string {
  if (!value) {
    return 'Not recorded'
  }

  const [date] = value.split('T')

  return date ?? value
}
