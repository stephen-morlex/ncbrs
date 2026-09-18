import { useCallback, useEffect, useState } from 'react'
import { useAuth } from 'react-oidc-context'
import { CircleAlert, Hospital, TriangleAlert } from 'lucide-react'
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
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'
import { realmRoles } from '@/auth/claims'
import { satisfies } from '@/auth/roles'
import { useApiClient } from '@/api/useApi'
import { PageHeader } from '@/shell/PageHeader'

// The console requests blocks as itself; the grant is attributed to it.
const WebClientDeviceId = 'ncbrs-web'

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
  const auth = useAuth()

  // Anyone may see how close a post is to running out; only someone permitted
  // to register births may top the block up. The server enforces both this and
  // the facility scope — a facility registrar can grant only their own — so a
  // hidden button is a courtesy, not the control.
  const canGrant = satisfies(realmRoles(auth.user), 'CanRegisterBirths')

  const [facilities, setFacilities] = useState<Facility[] | null>(null)
  const [total, setTotal] = useState(0)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(true)
  const [granting, setGranting] = useState<Facility | null>(null)

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
                    {canGrant ? <TableHead className="text-right">Grant</TableHead> : null}
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
                      {canGrant ? (
                        <TableCell className="text-right">
                          <Button variant="outline" size="sm" onClick={() => setGranting(facility)}>
                            Grant a block
                          </Button>
                        </TableCell>
                      ) : null}
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </CardContent>
        </Card>
      ) : null}

      {granting ? (
        <GrantBlockDialog
          facility={granting}
          onClose={() => setGranting(null)}
          onGranted={() => {
            setGranting(null)
            void load()
          }}
        />
      ) : null}
    </div>
  )
}

type GrantPhase =
  | { kind: 'form' }
  | { kind: 'submitting' }
  | { kind: 'granted'; start: number; end: number }
  | { kind: 'error'; error: NcbrsError }

/**
 * Grant a facility a fresh block of registration numbers.
 *
 * Blocks are handed out ahead of connectivity so a post can register offline
 * for weeks. The range comes back so the granter can see what was allocated;
 * an exhausted ceiling (409) is surfaced rather than silently doing nothing,
 * because raising it is a separate central act.
 */
function GrantBlockDialog({
  facility,
  onClose,
  onGranted,
}: {
  facility: Facility
  onClose: () => void
  onGranted: () => void
}) {
  const api = useApiClient()

  const [blockSize, setBlockSize] = useState(1000)
  const [phase, setPhase] = useState<GrantPhase>({ kind: 'form' })

  const busy = phase.kind === 'submitting'
  const invalid = !Number.isInteger(blockSize) || blockSize < 1 || blockSize > 10_000

  const submit = useCallback(async () => {
    if (invalid) {
      return
    }

    setPhase({ kind: 'submitting' })

    try {
      const { data, error: failure, response } = await api.POST(
        '/api/BirthRecords/{facilityId}/request-brn-block',
        {
          params: { path: { facilityId: facility.facilityId } },
          body: { data: { blockSize, deviceId: WebClientDeviceId } },
        },
      )

      if (response.ok && data?.data) {
        setPhase({ kind: 'granted', start: data.data.blockStart, end: data.data.blockEnd })
        return
      }

      setPhase({ kind: 'error', error: toNcbrsError(failure, response.status) })
    } catch (cause) {
      setPhase({ kind: 'error', error: unreachableError(cause) })
    }
  }, [api, facility.facilityId, blockSize, invalid])

  const granted = phase.kind === 'granted'

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : (granted ? onGranted() : onClose()))}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Grant a block to {facility.name}</DialogTitle>
          <DialogDescription>
            Registration numbers are handed out ahead of connectivity, so a post can register while
            offline.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          {phase.kind === 'error' ? (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertTitle>{phase.error.title}</AlertTitle>
              <AlertDescription>
                {phase.error.unreachable
                  ? 'The registry did not answer. Nothing was granted; try again.'
                  : phase.error.fields.map((item) => item.message).join(' ') || 'Nothing was granted.'}
              </AlertDescription>
            </Alert>
          ) : null}

          {phase.kind === 'granted' ? (
            <Alert>
              <AlertTitle>Block granted</AlertTitle>
              <AlertDescription>
                Numbers {phase.start.toLocaleString()}–{phase.end.toLocaleString()} are now this
                facility's to issue.
              </AlertDescription>
            </Alert>
          ) : (
            <div className="grid gap-2">
              <Label htmlFor="block-size">How many numbers</Label>
              <Input
                id="block-size"
                type="number"
                min={1}
                max={10000}
                value={blockSize}
                onChange={(event) => setBlockSize(event.target.valueAsNumber)}
                className="w-40"
                aria-invalid={invalid || undefined}
                disabled={busy}
              />
              <p className="text-muted-foreground text-xs">Between 1 and 10,000.</p>
            </div>
          )}
        </div>

        <DialogFooter>
          {granted ? (
            <Button onClick={onGranted}>Done</Button>
          ) : (
            <>
              <Button variant="outline" onClick={onClose} disabled={busy}>
                Cancel
              </Button>
              <Button onClick={() => void submit()} disabled={busy || invalid}>
                {busy ? <Spinner /> : null}
                Grant block
              </Button>
            </>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
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
