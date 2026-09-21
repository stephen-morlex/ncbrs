import { type FormEvent, type ReactNode, useCallback, useEffect, useState } from 'react'
import { Bar, BarChart, CartesianGrid, Cell, Line, LineChart, XAxis, YAxis } from 'recharts'
import { CircleAlert, Hourglass, RefreshCw, TriangleAlert } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import {
  type ChartConfig,
  ChartContainer,
  ChartLegend,
  ChartLegendContent,
  ChartTooltip,
  ChartTooltipContent,
} from '@/components/ui/chart'
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
type District = components['schemas']['CountySummary']
type TrendPoint = components['schemas']['TrendPoint']
type TierDelay = components['schemas']['TierRegistrationDelay']

const National = 'national'
const PollMs = 30_000

type Query = { countyCode: string; from: string; to: string }

/**
 * The Ministry's dashboard, over the reporting projection — a live, charted
 * overview rather than a table of the current period.
 *
 * Two rules from the read model survive into every chart here, and both are
 * easy to lose the moment data becomes a shape on a screen:
 *
 * **An indicator whose inputs are unknown reads "not available", never zero.**
 * A null share is a gap in the line, not a plunge to the axis; a null rate is
 * "not available" on the tile, not a reassuring 0.
 *
 * **The most recent month is still filling.** Registrations for it keep
 * arriving, so its figure only rises — the still-filling bar is faded and
 * flagged, so a chart does not show a fall in births that is only the month
 * not being over.
 */
export function Dashboard() {
  const consumer = useConsumerClient()

  const [summary, setSummary] = useState<Summary | null>(null)
  const [trends, setTrends] = useState<TrendPoint[]>([])
  const [districts, setDistricts] = useState<District[]>([])
  const [error, setError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(true)
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null)

  const [applied, setApplied] = useState<Query>({ countyCode: National, from: '', to: '' })
  const [live, setLive] = useState(true)
  const [draft, setDraft] = useState({ from: '', to: '' })

  const load = useCallback(
    async (query: Query, background = false) => {
      if (!background) {
        setLoading(true)
      }
      setError(null)

      const scoped = {
        ...(query.countyCode !== National ? { countyCode: query.countyCode } : {}),
        ...(query.from ? { from: query.from } : {}),
        ...(query.to ? { to: query.to } : {}),
      }
      const dates = {
        ...(query.from ? { from: query.from } : {}),
        ...(query.to ? { to: query.to } : {}),
      }

      try {
        const [summaryResult, trendsResult, districtsResult] = await Promise.all([
          consumer.GET('/api/dashboard/summary', { params: { query: scoped } }),
          consumer.GET('/api/dashboard/trends', { params: { query: scoped } }),
          consumer.GET('/api/dashboard/counties', { params: { query: dates } }),
        ])

        // The summary is the screen; if it fails, the screen failed. Trends and
        // the district comparison are enrichments — a failure there leaves a
        // chart empty rather than taking the dashboard down.
        if (summaryResult.error || !summaryResult.response.ok) {
          setError(toNcbrsError(summaryResult.error, summaryResult.response.status))
          return
        }

        setSummary(summaryResult.data ?? null)
        setTrends(trendsResult.response.ok ? (trendsResult.data ?? []) : [])
        setDistricts(districtsResult.response.ok ? (districtsResult.data ?? []) : [])
        setLastUpdated(new Date())
      } catch (cause) {
        setError(unreachableError(cause))
      } finally {
        setLoading(false)
      }
    },
    [consumer],
  )

  // Reload when the applied query changes, and — while Live — poll on an
  // interval. The consumer has no push channel, so "live" is polling; the
  // refresh is a background one, so the charts do not flash a skeleton every
  // thirty seconds.
  useEffect(() => {
    void load(applied)

    if (!live) {
      return
    }

    const id = setInterval(() => void load(applied, true), PollMs)
    return () => clearInterval(id)
  }, [applied, live, load])

  function selectDistrict(countyCode: string) {
    setApplied((current) => ({ ...current, countyCode }))
  }

  function applyDates(event: FormEvent) {
    event.preventDefault()
    setApplied((current) => ({ ...current, from: draft.from, to: draft.to }))
  }

  return (
    <div className="mx-auto w-full max-w-6xl space-y-6">
      <PageHeader
        title="Dashboard"
        description="Vital-statistics indicators over the reporting projection. Figures that cannot be computed read “not available”, never zero."
      />

      <Controls
        countyCode={applied.countyCode}
        districts={districts}
        onDistrict={selectDistrict}
        draft={draft}
        onDraft={setDraft}
        onApplyDates={applyDates}
        live={live}
        onToggleLive={() => setLive((value) => !value)}
        onRefresh={() => void load(applied, true)}
        lastUpdated={lastUpdated}
      />

      {loading && summary === null ? <LoadingDashboard /> : null}

      {error ? <Failure error={error} onRetry={() => void load(applied)} /> : null}

      {summary && !error ? <DashboardBody summary={summary} trends={trends} districts={districts} /> : null}
    </div>
  )
}

function Controls({
  countyCode,
  districts,
  onDistrict,
  draft,
  onDraft,
  onApplyDates,
  live,
  onToggleLive,
  onRefresh,
  lastUpdated,
}: {
  countyCode: string
  districts: District[]
  onDistrict: (id: string) => void
  draft: { from: string; to: string }
  onDraft: (value: { from: string; to: string }) => void
  onApplyDates: (event: FormEvent) => void
  live: boolean
  onToggleLive: () => void
  onRefresh: () => void
  lastUpdated: Date | null
}) {
  return (
    <Card>
      <CardContent className="flex flex-wrap items-end justify-between gap-4 pt-6">
        <div className="flex flex-wrap items-end gap-4">
          <div className="grid gap-2">
            <Label htmlFor="dash-district">Area</Label>
            <Select value={countyCode} onValueChange={onDistrict}>
              <SelectTrigger id="dash-district" className="w-56">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={National}>National</SelectItem>
                {districts.map((district) => (
                  <SelectItem key={district.countyCode} value={district.countyCode}>
                    {district.countyCode}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          <form onSubmit={onApplyDates} className="flex flex-wrap items-end gap-3">
            <div className="grid gap-2">
              <Label htmlFor="dash-from">From</Label>
              <Input
                id="dash-from"
                type="date"
                value={draft.from}
                onChange={(e) => onDraft({ ...draft, from: e.target.value })}
              />
            </div>
            <div className="grid gap-2">
              <Label htmlFor="dash-to">To</Label>
              <Input
                id="dash-to"
                type="date"
                value={draft.to}
                onChange={(e) => onDraft({ ...draft, to: e.target.value })}
              />
            </div>
            <Button type="submit" variant="outline">
              Apply
            </Button>
          </form>
        </div>

        <div className="flex items-center gap-3">
          <span className="text-muted-foreground text-xs" aria-live="polite">
            {lastUpdated ? `Updated ${lastUpdated.toLocaleTimeString()}` : 'Loading…'}
          </span>
          <Button variant="outline" size="sm" onClick={onRefresh}>
            <RefreshCw />
            Refresh
          </Button>
          <Button
            variant={live ? 'default' : 'outline'}
            size="sm"
            onClick={onToggleLive}
            aria-pressed={live}
          >
            {live ? 'Live' : 'Paused'}
          </Button>
        </div>
      </CardContent>
    </Card>
  )
}

function DashboardBody({
  summary,
  trends,
  districts,
}: {
  summary: Summary
  trends: TrendPoint[]
  districts: District[]
}) {
  return (
    <div className="space-y-6">
      <p className="text-muted-foreground text-sm">
        {formatDate(summary.period.fromUtc)} to {formatDate(summary.period.toUtc)}
        {summary.countyCode ? ` · ${summary.countyCode}` : ' · National'}
      </p>

      {summary.period.stillFilling ? (
        <Alert>
          <Hourglass />
          <AlertTitle>This period is still filling</AlertTitle>
          <AlertDescription>
            Registrations for births inside it are still arriving, so these figures will only rise.
            The most recent month is faded on the charts for the same reason.
          </AlertDescription>
        </Alert>
      ) : null}

      <KpiRow summary={summary} />

      <div className="grid gap-6 lg:grid-cols-2">
        <RegistrationsChart trends={trends} />
        <TimelinessChart trends={trends} />
        <MortalityChart trends={trends} />
        <DistrictChart districts={districts} />
      </div>

      <Indicators summary={summary} />

      <TierDelayCard delays={summary.registrationDelay.byFacilityTier} />

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

function KpiRow({ summary }: { summary: Summary }) {
  return (
    <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
      <Kpi label="Live births" value={summary.registrations.liveBirths.toLocaleString()} />
      <Kpi label="Registered on time" value={share(summary.timeliness.withinWindowShare)} />
      <Kpi
        label="Neonatal deaths"
        value={summary.mortality.neonatalDeaths.toLocaleString()}
        sub={<>{rate(summary.mortality.neonatalDeathsPerThousandLiveBirths, 'per 1,000 live births')}</>}
      />
      <Kpi label="Sync registered" value={share(summary.sync.registeredShare)} />
    </div>
  )
}

function Kpi({ label, value, sub }: { label: string; value: ReactNode; sub?: ReactNode }) {
  return (
    <Card>
      <CardContent className="pt-6">
        <p className="text-muted-foreground text-xs uppercase tracking-wide">{label}</p>
        <p className="text-2xl font-semibold">{value}</p>
        {sub ? <p className="text-muted-foreground text-xs">{sub}</p> : null}
      </CardContent>
    </Card>
  )
}

const registrationsConfig = {
  liveBirths: { label: 'Live births', color: 'var(--chart-1)' },
  fetalDeaths: { label: 'Fetal deaths', color: 'var(--chart-3)' },
} satisfies ChartConfig

function RegistrationsChart({ trends }: { trends: TrendPoint[] }) {
  const data = trends.map((point) => ({
    month: monthLabel(point.period.fromUtc),
    liveBirths: point.liveBirths,
    fetalDeaths: point.fetalDeaths,
    stillFilling: point.period.stillFilling,
  }))

  return (
    <ChartCard title="Registrations by month" note="Counted by date of occurrence. The faded month is still filling.">
      <ChartContainer config={registrationsConfig} className="h-64 w-full">
        <BarChart data={data} accessibilityLayer>
          <CartesianGrid vertical={false} />
          <XAxis dataKey="month" tickLine={false} axisLine={false} tickMargin={8} />
          <YAxis allowDecimals={false} width={36} />
          <ChartTooltip content={<ChartTooltipContent />} />
          <ChartLegend content={<ChartLegendContent />} />
          <Bar dataKey="liveBirths" stackId="a" fill="var(--color-liveBirths)" radius={[0, 0, 0, 0]}>
            {data.map((point) => (
              <Cell key={point.month} fillOpacity={point.stillFilling ? 0.4 : 1} />
            ))}
          </Bar>
          <Bar dataKey="fetalDeaths" stackId="a" fill="var(--color-fetalDeaths)" radius={[2, 2, 0, 0]}>
            {data.map((point) => (
              <Cell key={point.month} fillOpacity={point.stillFilling ? 0.4 : 1} />
            ))}
          </Bar>
        </BarChart>
      </ChartContainer>
    </ChartCard>
  )
}

const timelinessConfig = {
  withinWindowShare: { label: 'On-time share', color: 'var(--chart-1)' },
} satisfies ChartConfig

function TimelinessChart({ trends }: { trends: TrendPoint[] }) {
  const data = trends.map((point) => ({
    month: monthLabel(point.period.fromUtc),
    // Null stays null: a month with no known window status is a gap in the
    // line (connectNulls is off), never a drop to zero.
    withinWindowShare: point.withinWindowShare,
  }))

  return (
    <ChartCard title="Registered on time" note="Share within the statutory window. A gap is a month with no known status — not zero.">
      <ChartContainer config={timelinessConfig} className="h-64 w-full">
        <LineChart data={data} accessibilityLayer>
          <CartesianGrid vertical={false} />
          <XAxis dataKey="month" tickLine={false} axisLine={false} tickMargin={8} />
          <YAxis domain={[0, 100]} unit="%" width={44} />
          <ChartTooltip content={<ChartTooltipContent />} />
          <Line
            dataKey="withinWindowShare"
            type="monotone"
            stroke="var(--color-withinWindowShare)"
            strokeWidth={2}
            dot={false}
            connectNulls={false}
          />
        </LineChart>
      </ChartContainer>
    </ChartCard>
  )
}

const mortalityConfig = {
  neonatalDeaths: { label: 'Neonatal', color: 'var(--chart-3)' },
  maternalDeaths: { label: 'Maternal', color: 'var(--chart-5)' },
} satisfies ChartConfig

function MortalityChart({ trends }: { trends: TrendPoint[] }) {
  const data = trends.map((point) => ({
    month: monthLabel(point.period.fromUtc),
    neonatalDeaths: point.neonatalDeaths,
    maternalDeaths: point.maternalDeaths,
  }))

  return (
    <ChartCard title="Recorded deaths by month" note="Counts of perinatal and maternal deaths recorded against the month's births.">
      <ChartContainer config={mortalityConfig} className="h-64 w-full">
        <LineChart data={data} accessibilityLayer>
          <CartesianGrid vertical={false} />
          <XAxis dataKey="month" tickLine={false} axisLine={false} tickMargin={8} />
          <YAxis allowDecimals={false} width={36} />
          <ChartTooltip content={<ChartTooltipContent />} />
          <ChartLegend content={<ChartLegendContent />} />
          <Line dataKey="neonatalDeaths" type="monotone" stroke="var(--color-neonatalDeaths)" strokeWidth={2} dot={false} />
          <Line dataKey="maternalDeaths" type="monotone" stroke="var(--color-maternalDeaths)" strokeWidth={2} dot={false} />
        </LineChart>
      </ChartContainer>
    </ChartCard>
  )
}

const districtConfig = {
  liveBirths: { label: 'Live births', color: 'var(--chart-2)' },
} satisfies ChartConfig

function DistrictChart({ districts }: { districts: District[] }) {
  const data = [...districts]
    .sort((a, b) => b.liveBirths - a.liveBirths)
    .slice(0, 12)
    .map((district) => ({ countyCode: district.countyCode, liveBirths: district.liveBirths }))

  if (data.length === 0) {
    return (
      <ChartCard title="Live births by district" note="The district drill-down behind the national view.">
        <p className="text-muted-foreground py-10 text-center text-sm">No districts in range.</p>
      </ChartCard>
    )
  }

  return (
    <ChartCard title="Live births by district" note="Largest first. Select an area above to drill in.">
      <ChartContainer config={districtConfig} className="h-64 w-full">
        <BarChart data={data} layout="vertical" accessibilityLayer margin={{ left: 12 }}>
          <CartesianGrid horizontal={false} />
          <XAxis type="number" allowDecimals={false} />
          <YAxis type="category" dataKey="countyCode" width={110} tickLine={false} axisLine={false} />
          <ChartTooltip content={<ChartTooltipContent />} />
          <Bar dataKey="liveBirths" fill="var(--color-liveBirths)" radius={[0, 4, 4, 0]} />
        </BarChart>
      </ChartContainer>
    </ChartCard>
  )
}

/**
 * The nullable figures the charts do not carry, kept as tiles so their
 * "not available" is as visible as any bar. The shares here are already
 * percentages from the server (0–100); they are not multiplied again.
 */
function Indicators({ summary }: { summary: Summary }) {
  const { registrations, timeToConfirmation, registrationDelay, mortality, duplicates } = summary

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Indicators</CardTitle>
      </CardHeader>
      <CardContent className="grid grid-cols-2 gap-x-6 gap-y-4 sm:grid-cols-3">
        <Tile label="Males per 100 females" value={nn(registrations.sexRatio, (v) => v.toFixed(1))} />
        <Tile label="Median days to confirmation" value={nn(timeToConfirmation.medianDays, days)} />
        <Tile
          label="Median days birth → registration"
          value={nn(registrationDelay.medianDaysBirthToRegistration, days)}
        />
        <Tile
          label="Median days registration → centre"
          value={nn(registrationDelay.medianDaysRegistrationToCentre, days)}
        />
        <Tile
          label="Maternal deaths / 100,000"
          value={nn(mortality.maternalDeathsPerHundredThousandLiveBirths, (v) => v.toFixed(1))}
        />
        <Tile label="Duplicates / 10,000" value={nn(duplicates.perTenThousandBirths, (v) => v.toFixed(1))} />
      </CardContent>
    </Card>
  )
}

function Tile({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div>
      <p className="text-muted-foreground text-xs uppercase tracking-wide">{label}</p>
      <p className="text-lg">{value}</p>
    </div>
  )
}

/**
 * The two delays, broken out by facility tier — the one view where they
 * separate. How long a family took to reach a registrar (birth → registration)
 * and how long the record then waited to reach the centre (registration →
 * centre) are different problems with different remedies, and they diverge most
 * by tier: a hospital terminal's sync lag is ~zero by construction, while for a
 * village post the second delay can be most of the total. Reading the combined
 * figure alone would say families near a village post are slow to register when
 * they are not.
 */
function TierDelayCard({ delays }: { delays: TierDelay[] }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Time to registration, by facility tier</CardTitle>
        <p className="text-muted-foreground text-sm">
          Median days a family took to reach a registrar, and days the record then waited to reach
          the centre. The two diverge most by tier; a hospital's sync lag is near zero by
          construction.
        </p>
      </CardHeader>
      <CardContent>
        {delays.length === 0 ? (
          <p className="text-muted-foreground py-6 text-center text-sm">
            No confirmed registrations in range.
          </p>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Facility tier</TableHead>
                <TableHead className="text-right">Measured</TableHead>
                <TableHead className="text-right">Birth → registration</TableHead>
                <TableHead className="text-right">Registration → centre</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {delays.map((tier) => (
                <TableRow key={tier.facilityTier}>
                  <TableCell className="font-medium">{humanizeTier(tier.facilityTier)}</TableCell>
                  <TableCell className="text-right tabular-nums">
                    {tier.measured.toLocaleString()}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {nn(tier.medianDaysBirthToRegistration, days)}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {nn(tier.medianDaysRegistrationToCentre, days)}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  )
}

/** "VillageHealthPost" → "Village health post"; leaves already-spaced values be. */
function humanizeTier(tier: string): string {
  const spaced = tier.replace(/([a-z])([A-Z])/g, '$1 $2')
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase()
}

function ChartCard({ title, note, children }: { title: string; note: string; children: ReactNode }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">{title}</CardTitle>
        <p className="text-muted-foreground text-sm">{note}</p>
      </CardHeader>
      <CardContent>{children}</CardContent>
    </Card>
  )
}

/** "Not available" — never a zero. */
function NotAvailable() {
  return <span className="text-muted-foreground italic">Not available</span>
}

function nn(value: number | null, render: (value: number) => string): ReactNode {
  return value === null ? <NotAvailable /> : <span>{render(value)}</span>
}

/** Shares arrive as percentages (0–100); render as-is with a sign, or NA. */
function share(value: number | null): ReactNode {
  return value === null ? <NotAvailable /> : <span>{value.toFixed(1)}%</span>
}

function rate(value: number | null, suffix: string): ReactNode {
  return value === null ? <NotAvailable /> : <span>{`${value.toFixed(1)} ${suffix}`}</span>
}

function days(value: number): string {
  return `${value.toFixed(1)} days`
}

const MonthNames = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

function monthLabel(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) {
    return ''
  }
  return `${MonthNames[date.getUTCMonth()]} ${String(date.getUTCFullYear()).slice(2)}`
}

function LoadingDashboard() {
  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
        {[0, 1, 2, 3].map((i) => (
          <Card key={i} aria-busy>
            <CardContent className="space-y-2 pt-6">
              <Skeleton className="h-3 w-20" />
              <Skeleton className="h-7 w-16" />
            </CardContent>
          </Card>
        ))}
      </div>
      <Card aria-busy>
        <CardContent className="pt-6">
          <Skeleton className="h-64 w-full" />
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
