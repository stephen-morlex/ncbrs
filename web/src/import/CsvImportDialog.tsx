import { type ChangeEvent, type ReactNode, useState } from 'react'
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
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'

export interface ImportRow {
  line: number
  problems: string[]
}

export interface ImportResult<TRow extends ImportRow> {
  applied: boolean
  valid: number
  invalid: number
  rows: TRow[]
}

/** What the registry answered, as openapi-fetch returns it. */
export interface ImportAnswer<TRow extends ImportRow> {
  data?: { data?: ImportResult<TRow> | null } | null
  error?: unknown
  response: { ok: boolean; status: number }
}

/**
 * Adding several things from one CSV file: facilities, or the people to bind
 * to them (plan §17 item 38). One dialog, so every import works the same way.
 *
 * **Checked first, always.** The registry checks every row and adds nothing.
 * Only a file that checks clean can be applied, and it is applied whole or not
 * at all, so nobody is left working out which rows went in.
 */
export function CsvImportDialog<TRow extends ImportRow>({
  title,
  description,
  noun,
  send,
  columns,
  done,
  onClose,
  onImported,
}: {
  title: string
  description: ReactNode
  /** What a row adds, singular and plural: ["facility", "facilities"]. */
  noun: [string, string]
  send: (csv: string, apply: boolean) => Promise<ImportAnswer<TRow>>
  columns: { header: string; cell: (row: TRow) => ReactNode }[]
  /** What an applied row says, for example its range of numbers. */
  done: (row: TRow) => ReactNode
  onClose: () => void
  onImported: (count: number) => void
}) {
  const [csv, setCsv] = useState<string | null>(null)
  const [fileName, setFileName] = useState('')
  const [result, setResult] = useState<ImportResult<TRow> | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [busy, setBusy] = useState(false)
  const [one, many] = noun

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

  async function submit(apply: boolean) {
    if (csv === null) {
      return
    }

    setBusy(true)
    setError(null)

    try {
      const { data, error: failure, response } = await send(csv, apply)
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
  const count = (n: number) => (n === 1 ? `1 ${one}` : `${n} ${many}`)

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : onClose())}>
      <DialogContent className="sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>{description}</DialogDescription>
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
              <AlertTitle>{count(result.valid)} added</AlertTitle>
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
                    {columns.map((column) => (
                      <TableHead key={column.header}>{column.header}</TableHead>
                    ))}
                    <TableHead>Result</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {result.rows.map((row) => (
                    <TableRow key={row.line}>
                      <TableCell className="align-top tabular-nums">{row.line}</TableCell>
                      {columns.map((column) => (
                        <TableCell key={column.header} className="align-top">
                          {column.cell(row)}
                        </TableCell>
                      ))}
                      <TableCell className="align-top">
                        {row.problems.length > 0 ? (
                          <span className="text-destructive">{row.problems.join(' ')}</span>
                        ) : result.applied ? (
                          done(row)
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
            <Button onClick={() => void submit(true)} disabled={busy}>
              {busy ? <Spinner /> : null}
              Add {count(result.valid)}
            </Button>
          ) : (
            <Button onClick={() => void submit(false)} disabled={csv === null || busy}>
              {busy ? <Spinner /> : null}
              Check the file
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
