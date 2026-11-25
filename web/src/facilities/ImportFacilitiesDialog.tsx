import { type ChangeEvent, useState } from 'react'
import { CircleAlert, CircleCheck } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
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
import type { components } from '@/api/generated/api'
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'

type Result = components['schemas']['ImportFacilitiesResponse']

/**
 * Add a district's facilities from one CSV file (plan §17 item 38).
 *
 * **Checked first, always.** The registry checks every row exactly as "Add a
 * facility" would, plus what only a file gets wrong (the same name or office
 * code twice), and adds nothing. Only a file that checks clean can be added,
 * and it is added whole or not at all, so nobody is left working out which
 * rows exist.
 */
export function ImportFacilitiesDialog({ onClose, onImported }: { onClose: () => void; onImported: (count: number) => void }) {
  const api = useApiClient()
  const [csv, setCsv] = useState<string | null>(null)
  const [fileName, setFileName] = useState('')
  const [result, setResult] = useState<Result | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [busy, setBusy] = useState(false)

  async function choose(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0]
    setResult(null)
    setError(null)
    if (!file) {
      setCsv(null)
      return
    }

    setFileName(file.name)
    setCsv(await file.text())
  }

  async function send(apply: boolean) {
    if (csv === null) {
      return
    }

    setBusy(true)
    setError(null)

    try {
      const { data, error: failure, response } = await api.POST('/api/facilities/import', {
        body: { data: { csv, apply } },
      })

      if (response.ok && data?.data) {
        setResult(data.data)
        if (data.data.applied) {
          onImported(data.data.valid)
        }
        return
      }

      setError(toNcbrsError(failure, response.status))
    } catch (cause) {
      setError(unreachableError(cause))
    } finally {
      setBusy(false)
    }
  }

  const clean = result !== null && !result.applied && result.invalid === 0 && result.valid > 0

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : onClose())}>
      <DialogContent className="sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>Add facilities from a file</DialogTitle>
          <DialogDescription>
            A CSV file with the columns <span className="font-mono">name, tier, connectivity, area</span> and
            optionally <span className="font-mono">office_code</span>. The area is the code of its county, payam,
            boma or village. The registry checks every row before anything is added.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <div className="grid gap-2">
            <Label htmlFor="import-file">File</Label>
            <Input id="import-file" type="file" accept=".csv,text/csv" onChange={(event) => void choose(event)} />
          </div>

          {error ? (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertTitle>{error.title}</AlertTitle>
              <AlertDescription>{error.fields.map((item) => item.message).join(' ')}</AlertDescription>
            </Alert>
          ) : null}

          {result?.applied ? (
            <Alert>
              <CircleCheck />
              <AlertTitle>{result.valid === 1 ? '1 facility added' : `${result.valid} facilities added`}</AlertTitle>
              <AlertDescription>Each has its county and its own range of registration numbers.</AlertDescription>
            </Alert>
          ) : result ? (
            <Alert variant={result.invalid > 0 ? 'destructive' : 'default'}>
              {result.invalid > 0 ? <CircleAlert /> : <CircleCheck />}
              <AlertTitle>
                {result.invalid > 0
                  ? `${result.invalid} of ${result.valid + result.invalid} rows need correcting`
                  : `${fileName}: all ${result.valid} rows can be added`}
              </AlertTitle>
              <AlertDescription>
                {result.invalid > 0
                  ? 'Nothing has been added. Correct the file and check it again.'
                  : 'Nothing has been added yet.'}
              </AlertDescription>
            </Alert>
          ) : null}

          {result && result.rows.length > 0 ? (
            <div className="max-h-80 overflow-auto">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Line</TableHead>
                    <TableHead>Name</TableHead>
                    <TableHead>Where</TableHead>
                    <TableHead>Result</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {result.rows.map((row) => (
                    <TableRow key={row.line}>
                      <TableCell className="align-top tabular-nums">{row.line}</TableCell>
                      <TableCell className="align-top">
                        {row.name}
                        {row.officeCode ? <span className="text-muted-foreground block font-mono text-xs">{row.officeCode}</span> : null}
                      </TableCell>
                      <TableCell className="align-top">
                        {row.areaName ?? '—'}
                        {row.countyCode ? <span className="text-muted-foreground block font-mono text-xs">{row.countyCode}</span> : null}
                      </TableCell>
                      <TableCell className="align-top">
                        {row.problems.length > 0 ? (
                          <span className="text-destructive">{row.problems.join(' ')}</span>
                        ) : row.brnBlockStart != null && row.brnBlockEnd != null ? (
                          `Added: ${row.brnBlockStart.toLocaleString()}–${row.brnBlockEnd.toLocaleString()}`
                        ) : (
                          'Ready'
                        )}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          ) : null}
        </div>

        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            {result?.applied ? 'Close' : 'Cancel'}
          </Button>
          {result?.applied ? null : clean ? (
            <Button onClick={() => void send(true)} disabled={busy}>
              {busy ? <Spinner /> : null}
              {result.valid === 1 ? 'Add 1 facility' : `Add ${result.valid} facilities`}
            </Button>
          ) : (
            <Button onClick={() => void send(false)} disabled={csv === null || busy}>
              {busy ? <Spinner /> : null}
              Check the file
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
