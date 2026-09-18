import { type FormEvent, useState } from 'react'
import { CircleAlert, Download, EyeOff, MapPinOff } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Spinner } from '@/components/ui/spinner'
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

type Export = components['schemas']['Dhis2Export']

function currentMonth(): string {
  const now = new Date()
  return `${now.getUTCFullYear()}-${String(now.getUTCMonth() + 1).padStart(2, '0')}`
}

/**
 * The DHIS2 aggregate export — a district-month dataValueSet, ready to import
 * into DHIS2.
 *
 * **Aggregation is not anonymity, and the suppressions are how this screen
 * stays honest about it.** A count of one, in a small district, for a rare
 * event, identifies a family — so a district below the threshold is withheld
 * whole. The withheld districts are listed *beside* the values, with the
 * reason, because a recipient who cannot tell a suppressed figure from an
 * absent one reads the gap as zero — and "zero stillbirths" is a very
 * different claim from "too few to publish safely". Districts with no org-unit
 * mapping are listed too, rather than dropped, so their births are not lost
 * from the national figures without anyone noticing.
 */
export function Dhis2Export() {
  const consumer = useConsumerClient()

  const [month, setMonth] = useState(currentMonth())
  const [result, setResult] = useState<Export | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(false)

  async function generate(event: FormEvent) {
    event.preventDefault()
    if (!month) {
      return
    }

    setLoading(true)
    setError(null)
    setResult(null)

    try {
      const { data, error: failure, response } = await consumer.GET('/api/exports/dhis2', {
        // DHIS2 periods are YYYYMM; the month input gives YYYY-MM.
        params: { query: { period: month.replace('-', '') } },
      })

      if (failure || !response.ok) {
        setError(toNcbrsError(failure, response.status))
        return
      }

      setResult(data ?? null)
    } catch (cause) {
      setError(unreachableError(cause))
    } finally {
      setLoading(false)
    }
  }

  function download() {
    if (!result) {
      return
    }

    try {
      const blob = new Blob([JSON.stringify(result.dataValueSet, null, 2)], { type: 'application/json' })
      const url = URL.createObjectURL(blob)
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = `ncbrs-dhis2-${result.dataValueSet.period}.json`
      anchor.click()
      URL.revokeObjectURL(url)
    } catch {
      // A browser that blocks the download is not a reason to fail the screen;
      // the values are on it to read regardless.
    }
  }

  return (
    <div className="mx-auto w-full max-w-5xl space-y-6">
      <PageHeader
        title="DHIS2 export"
        description="A district-month aggregate for DHIS2. It shares no personal data — the unit is a district, never a person."
      />

      <Card>
        <CardContent className="pt-6">
          <form onSubmit={generate} className="flex flex-wrap items-end gap-3">
            <div className="grid gap-2">
              <Label htmlFor="export-month">Month</Label>
              <Input
                id="export-month"
                type="month"
                value={month}
                onChange={(event) => setMonth(event.target.value)}
                className="w-48"
              />
            </div>
            <Button type="submit" disabled={loading || !month}>
              {loading ? <Spinner /> : null}
              Generate
            </Button>
            {result ? (
              <Button type="button" variant="outline" onClick={download}>
                <Download />
                Download dataValueSet
              </Button>
            ) : null}
          </form>
        </CardContent>
      </Card>

      {error ? (
        <Alert variant="destructive">
          <CircleAlert />
          <AlertTitle>{error.title}</AlertTitle>
          <AlertDescription>
            {error.unreachable
              ? 'The reporting service did not answer. Check the connection before trying again.'
              : error.fields.map((item) => item.message).join(' ') ||
                'That period could not be exported. Use a real calendar month.'}
          </AlertDescription>
        </Alert>
      ) : null}

      {result ? <ExportResult result={result} /> : null}
    </div>
  )
}

function ExportResult({ result }: { result: Export }) {
  const values = result.dataValueSet.dataValues

  return (
    <div className="space-y-6">
      <p className="text-muted-foreground text-sm">
        Period {result.dataValueSet.period} · {plural(values.length, 'value')} ·{' '}
        {plural(result.suppressed.length, 'district')} withheld ·{' '}
        {plural(result.unmapped.length, 'district')} unmapped
      </p>

      {/* Suppressions first, and never as a footnote: the whole disclosure
          argument is that a withheld figure must not be mistaken for a zero. */}
      {result.suppressed.length > 0 ? (
        <Alert>
          <EyeOff />
          <AlertTitle>{plural(result.suppressed.length, 'district')} withheld — not zero</AlertTitle>
          <AlertDescription>
            <p className="mb-2">
              Below the safe cell size, a count identifies a family, so these districts are withheld
              whole — total included. A withheld figure means “too few to publish safely”, not zero.
            </p>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Org unit</TableHead>
                  <TableHead>Reason</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {result.suppressed.map((item) => (
                  <TableRow key={item.orgUnit}>
                    <TableCell className="font-mono text-xs">{item.orgUnit}</TableCell>
                    <TableCell>{item.reason}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </AlertDescription>
        </Alert>
      ) : null}

      {result.unmapped.length > 0 ? (
        <Alert variant="destructive">
          <MapPinOff />
          <AlertTitle>{plural(result.unmapped.length, 'district')} with no org-unit mapping</AlertTitle>
          <AlertDescription>
            <p className="mb-2">
              These districts have births but no DHIS2 org unit configured, so their figures reach
              no national total. They are listed rather than dropped — a silent gap would be worse.
            </p>
            <div className="flex flex-wrap gap-2">
              {result.unmapped.map((districtId) => (
                <Badge key={districtId} variant="outline" className="font-mono">
                  {districtId}
                </Badge>
              ))}
            </div>
          </AlertDescription>
        </Alert>
      ) : null}

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Data values</CardTitle>
        </CardHeader>
        <CardContent>
          {values.length === 0 ? (
            <p className="text-muted-foreground text-sm">
              No values for this period. With every district either below the threshold or unmapped,
              an empty set is a real answer — check the withheld and unmapped lists above.
            </p>
          ) : (
            <div className="overflow-x-auto">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Org unit</TableHead>
                    <TableHead>Data element</TableHead>
                    <TableHead className="text-right">Value</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {values.map((value, index) => (
                    <TableRow key={`${value.orgUnit}-${value.dataElement}-${index}`}>
                      <TableCell className="font-mono text-xs">{value.orgUnit}</TableCell>
                      <TableCell className="font-mono text-xs">{value.dataElement}</TableCell>
                      <TableCell className="text-right font-mono">{value.value}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  )
}

function plural(count: number, noun: string): string {
  return `${count.toLocaleString()} ${noun}${count === 1 ? '' : 's'}`
}
