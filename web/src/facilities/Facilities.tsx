import { useCallback, useEffect, useState } from 'react'
import { CircleAlert, Hospital, TriangleAlert } from 'lucide-react'
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
import { Skeleton } from '@/components/ui/skeleton'
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
import { PageHeader } from '@/shell/PageHeader'

type Facility = components['schemas']['FacilityResponse']
type BlockStatus = components['schemas']['BrnBlockStatus']

/**
 * The facilities in the caller's district, ordered so the ones about to stop
 * issuing real registration numbers are read first.
 *
 * **This screen is a warning, not a directory.** A facility that exhausts its
 * BRN block starts issuing `PROV-` identifiers: the birth is still
 * registered, but the family leaves with a slip rather than a certificate and
 * the record waits for a central act. Seeing that coming is the difference
 * between granting a block and explaining to a district why forty families
 * are holding provisional paper.
 */
export function Facilities() {
  const api = useApiClient()

  const [facilities, setFacilities] = useState<Facility[] | null>(null)
  const [total, setTotal] = useState(0)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(true)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)

    try {
      const { data, error: failure, response } = await api.GET('/api/facilities', {
        params: { query: { limit: 200 } },
      })

      if (failure || !response.ok) {
        setError(toNcbrsError(failure, response.status))
        return
      }

      setFacilities(data?.data?.items ?? [])
      setTotal(data?.data?.total ?? 0)
    } catch (cause) {
      setError(unreachableError(cause))
    } finally {
      setLoading(false)
    }
  }, [api])

  // Loaded on arrival: there is nothing to ask for, and making a district
  // officer press a button to find out whether a post is about to run out is
  // friction on the one thing this page is for.
  useEffect(() => {
    void load()
  }, [load])

  // Exhausted first, then low, then the rest — and within each, fewest
  // numbers left. A district officer reading top to bottom meets the posts
  // that are already handing out slips before the ones that are merely
  // approaching it.
  const ordered = [...(facilities ?? [])].sort((a, b) => {
    const rank = (status: BlockStatus) => (status === 'Exhausted' ? 0 : status === 'Low' ? 1 : 2)

    return rank(a.blockStatus) - rank(b.blockStatus) || a.brnRemaining - b.brnRemaining
  })

  const needingAttention = ordered.filter((facility) => facility.blockStatus !== 'Healthy').length

  return (
    <div className="mx-auto w-full max-w-5xl space-y-6">
      <PageHeader
        title="Facilities"
        description="Where births are registered, and how close each is to running out of registration numbers."
      />

      {loading && facilities === null ? <LoadingFacilities /> : null}

      {error ? <Failure error={error} onRetry={() => void load()} /> : null}

      {facilities !== null && !error ? (
        <Card>
          <CardContent className="pt-6">
            <p className="text-muted-foreground mb-3 text-sm">
              {needingAttention > 0
                ? `${needingAttention} of ${plural(total)} ${
                    needingAttention === 1 ? 'needs' : 'need'
                  } a block granting.`
                : `${plural(total)}, all with numbers in hand.`}
            </p>

            {ordered.length === 0 ? (
              <Empty>
                <EmptyHeader>
                  <EmptyMedia variant="icon">
                    <Hospital />
                  </EmptyMedia>
                  <EmptyTitle>No facilities in your district</EmptyTitle>
                  <EmptyDescription>
                    A district with no facilities registers no births, which is worth checking
                    rather than accepting.
                  </EmptyDescription>
                </EmptyHeader>
              </Empty>
            ) : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Facility</TableHead>
                    <TableHead>Tier</TableHead>
                    <TableHead>Connectivity</TableHead>
                    <TableHead className="text-right">Numbers left</TableHead>
                    <TableHead>Block</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {ordered.map((facility) => (
                    <TableRow key={facility.facilityId}>
                      <TableCell className="font-medium">{facility.name}</TableCell>
                      <TableCell>{spaced(facility.tier)}</TableCell>
                      <TableCell>{spaced(facility.connectivityProfile)}</TableCell>
                      <TableCell className="text-right font-mono">
                        {facility.brnRemaining.toLocaleString()}
                        {/* The threshold beside the count, because the same
                            number means different things at different
                            connectivity profiles and a verdict without its
                            reason looks arbitrary. */}
                        <span className="text-muted-foreground block text-xs">
                          warn below {facility.brnWarnBelow.toLocaleString()}
                        </span>
                      </TableCell>
                      <TableCell>
                        <BlockBadge status={facility.blockStatus} />
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </CardContent>
        </Card>
      ) : null}
    </div>
  )
}

/**
 * Three states rather than a bar or a percentage: the action differs at each,
 * and a percentage invites the reader to invent their own threshold — which
 * is exactly the mistake the per-profile thresholds exist to prevent.
 */
function BlockBadge({ status }: { status: BlockStatus }) {
  if (status === 'Exhausted') {
    return <Badge variant="destructive">issuing provisional numbers</Badge>
  }

  if (status === 'Low') {
    return <Badge variant="outline">grant a block</Badge>
  }

  return (
    <Badge variant="secondary" className="text-muted-foreground">
      in hand
    </Badge>
  )
}

function LoadingFacilities() {
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
            : error.fields.map((item) => item.message).join(' ')}
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
 * "1 facilities" is the kind of small wrongness that makes a screen feel
 * unfinished — and a district with a single facility is not an edge case
 * here, it is most of them.
 */
function plural(count: number): string {
  return `${count.toLocaleString()} ${count === 1 ? 'facility' : 'facilities'}`
}

/** `VillageHealthPost` reads as a token; "Village Health Post" reads as English. */
function spaced(value: string): string {
  return value.replace(/([a-z])([A-Z])/g, '$1 $2')
}
