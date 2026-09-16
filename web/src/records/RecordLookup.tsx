import { type FormEvent, useCallback, useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { CircleAlert, FileSearch, History, Search, TriangleAlert } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from '@/components/ui/empty'
import { Input } from '@/components/ui/input'
import { Item, ItemContent, ItemDescription, ItemGroup, ItemTitle } from '@/components/ui/item'
import { Label } from '@/components/ui/label'
import { Separator } from '@/components/ui/separator'
import { Skeleton } from '@/components/ui/skeleton'
import { Spinner } from '@/components/ui/spinner'
import type { components } from '@/api/generated/api'
import { type NcbrsError, messagesFor, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'
import { PageHeader } from '@/shell/PageHeader'

type BirthRecord = components['schemas']['BirthRecordResponse']

const BrnField = 'brn'

/**
 * The Phase 1 vertical slice: look up a BRN and show the record.
 *
 * One screen, but it is the first time the whole stack runs end to end -- a
 * real access token, a real CORS preflight, the `ncbrs-api` audience the
 * token must carry, the `{ meta, data }` envelope, and types generated from
 * the API's own document. Any of those five can be wrong, and until now none
 * had been exercised together.
 *
 * The lookup resolves on **either** a BRN or a provisional identifier, which
 * the API handles: a family may still be holding the slip a device printed
 * before the record was reconciled, and telling them that number is not
 * recognised would be wrong.
 */
export function RecordLookup() {
  const api = useApiClient()

  // A search result links here with ?brn=…, so the number arrives in the URL
  // rather than being typed. That also makes a record shareable: a registrar
  // sending a colleague a link is the commonest way one is opened twice.
  const [params, setParams] = useSearchParams()
  const linked = params.get('brn') ?? ''

  const [query, setQuery] = useState(linked)
  const [record, setRecord] = useState<BirthRecord | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [searching, setSearching] = useState(false)
  const [searched, setSearched] = useState(false)

  const fetchRecord = useCallback(
    async (brn: string) => {
      if (!brn) {
        return
      }

      setSearching(true)
      setError(null)
      setRecord(null)

      try {
        const { data, error: failure, response } = await api.GET('/api/BirthRecords/{brn}', {
          params: { path: { brn } },
        })

        if (failure || !response.ok) {
          setError(toNcbrsError(failure, response.status))
        } else {
          setRecord(data?.data ?? null)
        }
      } catch (cause) {
        // Never reached the server at all: offline, DNS, or a refused CORS
        // preflight. Distinct from a refusal, and the distinction is the
        // difference between "try again" and "this will not work".
        setError(unreachableError(cause))
      } finally {
        setSearching(false)
        setSearched(true)
      }
    },
    [api],
  )

  // Opens the record named in the URL. Depends on the value, not the params
  // object, so typing in the box afterwards does not refetch the linked one.
  useEffect(() => {
    if (linked) {
      void fetchRecord(linked)
    }
  }, [linked, fetchRecord])

  function onSubmit(event: FormEvent) {
    event.preventDefault()

    const brn = query.trim()

    if (!brn) {
      return
    }

    // Through the URL rather than straight to the fetch, so a typed lookup
    // and a linked one are the same thing and the address bar always names
    // the record on screen.
    setParams(brn === linked ? params : { brn })

    if (brn === linked) {
      void fetchRecord(brn)
    }
  }

  const fieldMessages = messagesFor(error, BrnField)

  return (
    <div className="mx-auto w-full max-w-3xl space-y-6">
      <PageHeader
        title="Find by number"
        description="By birth registration number, or by the provisional identifier a device issued before the record was reconciled."
      />

      <Card>
        <CardContent className="pt-6">
          <form onSubmit={onSubmit} className="grid gap-2">
            <Label htmlFor={BrnField}>Registration number</Label>
            <div className="flex flex-col gap-2 sm:flex-row">
              <Input
                id={BrnField}
                name={BrnField}
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                placeholder="BRN-2026-000123 or PROV-…"
                autoComplete="off"
                className="sm:flex-1"
                // The server names this field when it objects, so the message
                // below is attached to the control it is about rather than
                // floating above the form.
                aria-invalid={fieldMessages.length > 0 || undefined}
                aria-describedby={fieldMessages.length > 0 ? `${BrnField}-error` : undefined}
              />
              <Button type="submit" disabled={searching || query.trim().length === 0}>
                {searching ? <Spinner /> : <Search />}
                Find
              </Button>
            </div>
            {fieldMessages.length > 0 ? (
              <p id={`${BrnField}-error`} className="text-destructive text-sm">
                {fieldMessages.join(' ')}
              </p>
            ) : null}
          </form>
        </CardContent>
      </Card>

      {searching ? <LoadingRecord /> : null}

      {!searching && error && fieldMessages.length === 0 ? <Failure error={error} /> : null}

      {!searching && record ? <RecordCard record={record} /> : null}

      {!searching && !error && !record && searched ? (
        <Empty className="border">
          <EmptyHeader>
            <EmptyMedia variant="icon">
              <FileSearch />
            </EmptyMedia>
            <EmptyTitle>No record for that number</EmptyTitle>
            <EmptyDescription>
              Check the number as printed. A number that has circulated always resolves, so a
              blank result usually means a transcription error.
            </EmptyDescription>
          </EmptyHeader>
        </Empty>
      ) : null}
    </div>
  )
}

function LoadingRecord() {
  return (
    <Card aria-busy>
      <CardHeader>
        <Skeleton className="h-6 w-56" />
        <Skeleton className="h-4 w-32" />
      </CardHeader>
      <CardContent className="space-y-3">
        <Skeleton className="h-14 w-full" />
        <Skeleton className="h-14 w-full" />
        <Skeleton className="h-14 w-full" />
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

function RecordCard({ record }: { record: BirthRecord }) {
  return (
    <Card>
      <CardHeader>
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="space-y-1">
            <CardTitle className="text-xl">{record.childFullName ?? 'Name not recorded'}</CardTitle>
            <CardDescription className="font-mono">{record.brn}</CardDescription>
          </div>
          <div className="flex flex-wrap gap-1.5">
            <Badge variant={record.annulment ? 'destructive' : 'secondary'}>{record.status}</Badge>
            {/* Confirmed means the centre reconciled the BRN against the block
                it actually granted. Unconfirmed is not an error -- the birth
                happened and the number may already be printed on a slip in a
                family's hands. */}
            {record.confirmedAtUtc ? null : <Badge variant="outline">BRN unconfirmed</Badge>}
            {record.lateRegistration ? <Badge variant="outline">late registration</Badge> : null}
          </div>
        </div>

        {/* Reachable from the record, which is where a dispute starts. Shown
            to everyone; the API refuses it to anyone without the policy, and
            hiding the link would leave a facility registrar unable to tell
            the history exists at all. */}
        <Button asChild variant="outline" size="sm" className="mt-2 w-fit">
          <Link to={`/audit?brn=${encodeURIComponent(record.brn)}`}>
            <History />
            This record's history
          </Link>
        </Button>
      </CardHeader>

      <CardContent className="space-y-4">
        {/* An annulled record still resolves and still shows why: a number
            that has circulated must keep answering with an explanation
            rather than falling silent. */}
        {record.annulment ? (
          <>
            <Item variant="outline" className="border-destructive/50">
              <ItemContent>
                <ItemTitle className="text-destructive">This registration was annulled</ItemTitle>
                <ItemDescription>
                  The register records no such birth. The number is kept so it keeps resolving.
                </ItemDescription>
              </ItemContent>
            </Item>
            <Separator />
          </>
        ) : null}

        <ItemGroup>
          <Detail label="Date of birth" value={formatDate(record.dateOfBirth)} />
          <Detail label="Sex" value={record.sex ?? 'Not recorded'} />
          <Detail
            label="BRN confirmed"
            value={
              record.confirmedAtUtc
                ? formatDate(record.confirmedAtUtc)
                : 'Not yet reconciled against the facility’s block'
            }
          />
        </ItemGroup>
      </CardContent>
    </Card>
  )
}

function Detail({ label, value }: { label: string; value: string }) {
  return (
    <Item variant="outline">
      <ItemContent>
        <ItemTitle className="text-muted-foreground text-xs font-normal uppercase tracking-wide">
          {label}
        </ItemTitle>
        <ItemDescription className="text-foreground text-sm">{value}</ItemDescription>
      </ItemContent>
    </Item>
  )
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
