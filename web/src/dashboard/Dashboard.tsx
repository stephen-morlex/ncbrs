import { type FormEvent, type ReactNode, useCallback, useEffect, useState } from 'react'
import { CircleAlert, Hourglass, TriangleAlert } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
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
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import type { components } from '@/api/generated/consumer'
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'
import { useConsumerClient } from '@/api/useApi'
import { PageHeader } from '@/shell/PageHeader'
import { formatDate } from '@/records/RecordDetail'

type Summary = components['schemas']['DashboardSummary']
type District = components['schemas']['DistrictSummary']

const National = 'national'

/**
 * The Ministry's dashboard, over the reporting projection.
 *
 * **The rule that outranks the arithmetic: an indicator whose inputs are
 * unknown reports "not available" and never zero.** A Ministry reading 0
 * neonatal deaths concludes the month went well; one reading "not available"
 * sends someone to find out. Every nullable figure the API returns is rendered
 * through {@link NotAvailable} for exactly that reason — a stray `0` here would
 * turn ignorance into a reassuring fact.
 *
 * A recent period is still filling: registrations for births inside it are
 * still arriving, so the figures only rise. That is flagged, not hidden, so a
 * rising line is not read as a recovery.
 */
export function Dashboard() {
  const consumer = useConsumerClient()

  const [summary, setSummary] = useState<Summary | null>(null)
  const [districts, setDistricts] = useState<District[]>([])
  const [error, setError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(true)

  const [districtId, setDistrictId] = useState<string>(National)
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')

  const load = useCallback(
    async (district: string, fromDate: string, toDate: string) => {
      setLoading(true)
      setError(null)

      try {
        const { data, error: failure, response } = await consumer.GET('/api/dashboard/summary', {
          params: {
            query: {
              ...(district !== National ? { districtId: district } : {}),
              ...(fromDate ? { from: fromDate } : {}),
              ...(toDate ? { to: toDate } : {}),
            },
          },
        })

        if (failure || !response.ok) {
          setError(toNcbrsError(failure, response.status))
          return
        }

        // The consumer returns the body unwrapped — no { data } envelope.
        setSummary(data ?? null)
      } catch (cause) {
        setError(unreachableError(cause))
      } finally {
        setLoading(false)
      }
    },
    [consumer],
  )

  useEffect(() => {
    let cancelled = false

    void (async () => {
      try {
        const { data, response } = await consumer.GET('/api/dashboard/districts', {})
        if (!cancelled && response.ok) {
          setDistricts(data ?? [])
        }
      } catch {
        // The drill-down is a convenience; the national view works without it.
      }
    })()

    return () => {
      cancelled = true
    }
  }, [consumer])

  // The first load is the national, current period. Changing the area reloads
  // at once; changing the dates waits for Apply, so a half-typed range does
  // not fire a query on every keystroke.
  useEffect(() => {
    void load(National, '', '')
  }, [load])

  function selectDistrict(value: string) {
    setDistrictId(value)
    void load(value, from, to)
  }

  function applyDates(event: FormEvent) {
    event.preventDefault()
    void load(districtId, from, to)
  }

  return (
    <div className="mx-auto w-full max-w-5xl space-y-6">
      <PageHeader
        title="Dashboard"
        description="Vital-statistics indicators over the reporting projection. Figures that cannot be computed read “not available”, never zero."
      />

      <Card>
        <CardContent className="flex flex-wrap items-end gap-4 pt-6">
          <div className="grid gap-2">
            <Label htmlFor="dash-district">Area</Label>
            <Select value={districtId} onValueChange={selectDistrict}>
              <SelectTrigger id="dash-district" className="w-56">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={National}>National</SelectItem>
                {districts.map((district) => (
                  <SelectItem key={district.districtId} value={district.districtId}>
                    {district.districtId}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          <form onSubmit={applyDates} className="flex flex-wrap items-end gap-3">
            <div className="grid gap-2">
              <Label htmlFor="dash-from">From</Label>
              <Input id="dash-from" type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
            </div>
            <div className="grid gap-2">
              <Label htmlFor="dash-to">To</Label>
              <Input id="dash-to" type="date" value={to} onChange={(e) => setTo(e.target.value)} />
            </div>
            <Button type="submit" variant="outline">
              Apply
            </Button>
          </form>
        </CardContent>
      </Card>

      {loading && summary === null ? <LoadingDashboard /> : null}

      {error ? <Failure error={error} onRetry={() => void load(districtId, from, to)} /> : null}

      {summary && !error ? <SummaryView summary={summary} /> : null}
    </div>
  )
}

function SummaryView({ summary }: { summary: Summary }) {
  const { period, registrations, timeliness, mortality, timeToConfirmation, registrationDelay, sync, duplicates } =
    summary

  return (
    <div className="space-y-6">
      <p className="text-muted-foreground text-sm">
        {formatDate(period.fromUtc)} to {formatDate(period.toUtc)}
        {summary.districtId ? ` · ${summary.districtId}` : ' · National'}
      </p>

      {period.stillFilling ? (
        <Alert>
          <Hourglass />
          <AlertTitle>This period is still filling</AlertTitle>
          <AlertDescription>
            Registrations for births inside it are still arriving, so these figures will only rise.
            Do not read the recent trend as a fall and recovery.
          </AlertDescription>
        </Alert>
      ) : null}

      <Section title="Registrations">
        <Metric label="Live births" value={num(registrations.liveBirths)} />
        <Metric label="Fetal deaths" value={num(registrations.fetalDeaths)} />
        <Metric
          label="Annulled"
          value={num(registrations.annulled)}
          hint="Excluded from every indicator; counted here on its own."
        />
        <Metric label="Male" value={num(registrations.male)} />
        <Metric label="Female" value={num(registrations.female)} />
        <Metric label="Sex ratio" value={nn(registrations.sexRatio, (v) => v.toFixed(2))} />
      </Section>

      <Section title="Timeliness" note="Share of registrations made inside the statutory window — not registration completeness.">
        <Metric label="Within window" value={num(timeliness.withinWindow)} />
        <Metric label="Outside window" value={num(timeliness.outsideWindow)} />
        <Metric label="Unknown" value={num(timeliness.unknown)} />
        <Metric label="Within-window share" value={nn(timeliness.withinWindowShare, pct)} />
      </Section>

      <Section title="Mortality" note="Rates report “not available” rather than zero when the inputs are unknown.">
        <Metric label="Neonatal deaths" value={num(mortality.neonatalDeaths)} />
        <Metric
          label="per 1,000 live births"
          value={nn(mortality.neonatalDeathsPerThousandLiveBirths, (v) => v.toFixed(1))}
        />
        <Metric label="Maternal deaths" value={num(mortality.maternalDeaths)} />
        <Metric
          label="per 100,000 live births"
          value={nn(mortality.maternalDeathsPerHundredThousandLiveBirths, (v) => v.toFixed(1))}
        />
      </Section>

      <Section title="Time to confirmation">
        <Metric label="Confirmed" value={num(timeToConfirmation.confirmed)} />
        <Metric
          label="Still unconfirmed"
          value={num(timeToConfirmation.stillUnconfirmed)}
          hint="Excluded from the median; treating a provisional record as zero days would flatter the offline tier."
        />
        <Metric label="Median days" value={nn(timeToConfirmation.medianDays, days)} />
      </Section>

      <Section title="Registration delay" note="The delay before a family reached a registrar and the delay a record then took to reach the centre are different problems, reported apart.">
        <Metric
          label="Birth → registration (median)"
          value={nn(registrationDelay.medianDaysBirthToRegistration, days)}
        />
        <Metric
          label="Registration → centre (median)"
          value={nn(registrationDelay.medianDaysRegistrationToCentre, days)}
        />
        <Metric label="Measured" value={num(registrationDelay.measured)} />
        <Metric label="Not measurable" value={num(registrationDelay.notMeasurable)} />
      </Section>

      {registrationDelay.byFacilityTier.length > 0 ? (
        <TierTable
          title="Registration delay by facility tier"
          caption="Where the two delays diverge most — a hospital terminal's sync lag is zero by construction."
          rows={registrationDelay.byFacilityTier.map((tier) => ({
            tier: tier.facilityTier,
            cells: [
              num(tier.measured),
              nn(tier.medianDaysBirthToRegistration, days),
              nn(tier.medianDaysRegistrationToCentre, days),
            ],
          }))}
          headers={['Tier', 'Measured', 'Birth → registration', 'Registration → centre']}
        />
      ) : null}

      <Section title="Sync reliability">
        <Metric label="Batches" value={num(sync.batches)} />
        <Metric label="Devices reporting" value={num(sync.devicesReporting)} />
        <Metric label="Records submitted" value={num(sync.recordsSubmitted)} />
        <Metric label="Registered" value={num(sync.recordsRegistered)} />
        <Metric label="Rejected" value={num(sync.recordsRejected)} />
        <Metric label="Registered share" value={nn(sync.registeredShare, pct)} />
      </Section>

      <Section title="Duplicates" note={duplicates.caveat}>
        <Metric label="Duplicates seen" value={num(duplicates.duplicatesSeen)} />
        <Metric label="per 10,000 births" value={nn(duplicates.perTenThousandBirths, (v) => v.toFixed(1))} />
      </Section>

      {summary.notAvailable.length > 0 ? (
        <Card>
          <CardHeader>
            <CardTitle className="text-base">Not available this period</CardTitle>
          </CardHeader>
          <CardContent>
            <p className="text-muted-foreground mb-2 text-sm">
              These §10 indicators cannot be produced from the data on hand. They are named rather
              than shown as zero, so a gap is read as a gap.
            </p>
            <div className="flex flex-wrap gap-2">
              {summary.notAvailable.map((indicator) => (
                <Badge key={indicator} variant="outline">
                  {indicator}
                </Badge>
              ))}
            </div>
          </CardContent>
        </Card>
      ) : null}
    </div>
  )
}

/** "Not available" — the one thing a nullable indicator must never render as 0. */
function NotAvailable() {
  return <span className="text-muted-foreground italic">Not available</span>
}

/** A whole number is always known; render it plainly. */
function num(value: number): ReactNode {
  return value.toLocaleString()
}

/** A nullable figure: the value formatted, or "Not available" — never zero. */
function nn(value: number | null, render: (value: number) => string): ReactNode {
  return value === null ? <NotAvailable /> : <span>{render(value)}</span>
}

function pct(value: number): string {
  return `${(value * 100).toFixed(1)}%`
}

function days(value: number): string {
  return `${value.toFixed(1)} days`
}

function Section({ title, note, children }: { title: string; note?: string; children: ReactNode }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">{title}</CardTitle>
        {note ? <p className="text-muted-foreground text-sm">{note}</p> : null}
      </CardHeader>
      <CardContent>
        <div className="grid grid-cols-2 gap-x-6 gap-y-4 sm:grid-cols-3">{children}</div>
      </CardContent>
    </Card>
  )
}

function Metric({ label, value, hint }: { label: string; value: ReactNode; hint?: string }) {
  return (
    <div>
      <p className="text-muted-foreground text-xs uppercase tracking-wide">{label}</p>
      <p className="text-lg">{value}</p>
      {hint ? <p className="text-muted-foreground text-xs">{hint}</p> : null}
    </div>
  )
}

function TierTable({
  title,
  caption,
  headers,
  rows,
}: {
  title: string
  caption: string
  headers: string[]
  rows: { tier: string; cells: ReactNode[] }[]
}) {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">{title}</CardTitle>
        <p className="text-muted-foreground text-sm">{caption}</p>
      </CardHeader>
      <CardContent>
        <Table>
          <TableHeader>
            <TableRow>
              {headers.map((header) => (
                <TableHead key={header}>{header}</TableHead>
              ))}
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((row) => (
              <TableRow key={row.tier}>
                <TableCell className="font-medium">{spaced(row.tier)}</TableCell>
                {row.cells.map((cell, index) => (
                  <TableCell key={index}>{cell}</TableCell>
                ))}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </CardContent>
    </Card>
  )
}

function spaced(value: string): string {
  return value.replace(/([a-z])([A-Z])/g, '$1 $2')
}

function LoadingDashboard() {
  return (
    <div className="space-y-4">
      <Card aria-busy>
        <CardContent className="space-y-3 pt-6">
          <Skeleton className="h-4 w-40" />
          <Skeleton className="h-24 w-full" />
        </CardContent>
      </Card>
      <Card aria-busy>
        <CardContent className="space-y-3 pt-6">
          <Skeleton className="h-24 w-full" />
        </CardContent>
      </Card>
    </div>
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
            ? 'The reporting service did not answer. Check the connection before trying again.'
            : error.fields.map((item) => item.message).join(' ') || 'The dashboard could not be loaded.'}
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
