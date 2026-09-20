import { useCallback, useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { Activity, CircleAlert, CircleCheck, ShieldOff } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Field, FieldDescription, FieldGroup, FieldLabel } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Spinner } from '@/components/ui/spinner'
import type { components } from '@/api/generated/api'
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'
import { PageHeader } from '@/shell/PageHeader'

type BirthRecord = components['schemas']['BirthRecordResponse']
type NeonatalResponse = components['schemas']['NeonatalOutcomeResponse']
type MaternalResponse = components['schemas']['MaternalOutcomeResponse']
type IcdPmTiming = NeonatalResponse['icdPmTiming']

const WebClientDeviceId = 'ncbrs-web'

type Kind = 'neonatal' | 'maternal'

/**
 * Recording a death that follows a birth event — a second vital event, per
 * WHO/UN standards, attached to the registration rather than editing it.
 *
 * Two kinds, because they answer different questions and are classified under
 * different WHO systems: a **neonatal** death of the child within 28 days
 * (ICD-PM), and a **maternal** death of the mother linked to the birth
 * (ICD-MM). The cause is always a code, never free text — that is what makes
 * the national statistics comparable.
 */
export function RecordOutcome() {
  const api = useApiClient()

  const [params] = useSearchParams()
  const brn = params.get('brn') ?? ''

  const [record, setRecord] = useState<BirthRecord | null>(null)
  const [loading, setLoading] = useState(true)
  const [kind, setKind] = useState<Kind>('neonatal')

  const [deathDate, setDeathDate] = useState('')
  const [timing, setTiming] = useState<IcdPmTiming>('Neonatal')
  const [causeCode, setCauseCode] = useState('')
  const [maternalCondition, setMaternalCondition] = useState('')

  const [error, setError] = useState<NcbrsError | null>(null)
  const [recorded, setRecorded] = useState<{ kind: Kind; daysAfterBirth: number } | null>(null)
  const [submitting, setSubmitting] = useState(false)

  useEffect(() => {
    let cancelled = false
    async function load() {
      if (!brn) {
        setLoading(false)
        return
      }
      try {
        const { data, error: failure, response } = await api.GET('/api/BirthRecords/{brn}', {
          params: { path: { brn } },
        })
        if (cancelled) return
        if (failure || !response.ok) {
          setError(toNcbrsError(failure, response.status))
        } else {
          setRecord(data?.data ?? null)
        }
      } catch (cause) {
        if (!cancelled) setError(unreachableError(cause))
      } finally {
        if (!cancelled) setLoading(false)
      }
    }
    void load()
    return () => {
      cancelled = true
    }
  }, [api, brn])

  const submit = useCallback(
    async (event: React.FormEvent) => {
      event.preventDefault()
      if (!deathDate || !causeCode) return

      setSubmitting(true)
      setError(null)

      // A date input gives a calendar day; the death is recorded at the start
      // of that day in UTC. The server bounds it below by the birth and above
      // by now (plus a small skew), so a future date is refused there.
      const deathDateUtc = `${deathDate}T00:00:00Z`

      try {
        if (kind === 'neonatal') {
          const { data, error: failure, response } = await api.POST(
            '/api/BirthRecords/{brn}/neonatal-outcome',
            {
              params: { path: { brn } },
              body: {
                data: {
                  deathDateUtc,
                  icdPmTiming: timing,
                  icdPmCauseCode: causeCode,
                  contributingMaternalConditionCode: maternalCondition || undefined,
                  deviceId: WebClientDeviceId,
                },
              },
            },
          )
          if (failure || !response.ok) {
            setError(toNcbrsError(failure, response.status))
            return
          }
          const result = data?.data as NeonatalResponse | undefined
          setRecorded({ kind, daysAfterBirth: result?.daysAfterBirth ?? 0 })
        } else {
          const { data, error: failure, response } = await api.POST(
            '/api/BirthRecords/{brn}/maternal-outcome',
            {
              params: { path: { brn } },
              body: {
                data: { deathDateUtc, icdMmCauseCode: causeCode, deviceId: WebClientDeviceId },
              },
            },
          )
          if (failure || !response.ok) {
            setError(toNcbrsError(failure, response.status))
            return
          }
          const result = data?.data as MaternalResponse | undefined
          setRecorded({ kind, daysAfterBirth: result?.daysAfterBirth ?? 0 })
        }
      } catch (cause) {
        setError(unreachableError(cause))
      } finally {
        setSubmitting(false)
      }
    },
    [api, brn, kind, deathDate, timing, causeCode, maternalCondition],
  )

  if (!brn) {
    return (
      <div className="mx-auto w-full max-w-3xl space-y-6">
        <PageHeader title="Record an outcome" description="Open a record first." />
        <Alert variant="destructive">
          <CircleAlert />
          <AlertTitle>No record named</AlertTitle>
          <AlertDescription>
            An outcome is recorded against a particular registration. Find the record first, then
            choose “Record an outcome”.
          </AlertDescription>
        </Alert>
      </div>
    )
  }

  if (loading) {
    return (
      <div className="mx-auto flex w-full max-w-3xl justify-center py-12">
        <Spinner />
      </div>
    )
  }

  if (recorded) {
    const label = recorded.kind === 'neonatal' ? 'Neonatal death' : 'Maternal death'
    return (
      <div className="mx-auto w-full max-w-3xl space-y-6">
        <PageHeader title="Outcome recorded" description={`Recorded against ${brn}.`} />
        <Alert>
          <CircleCheck />
          <AlertTitle>{label} recorded</AlertTitle>
          <AlertDescription>
            {recorded.daysAfterBirth} day{recorded.daysAfterBirth === 1 ? '' : 's'} after the birth.
            The registration itself is unchanged — this is a separate vital event.
          </AlertDescription>
        </Alert>
        <Button asChild variant="outline">
          <Link to={`/records?brn=${encodeURIComponent(brn)}`}>Back to the record</Link>
        </Button>
      </div>
    )
  }

  // An annulled record names no birth to attach an outcome to; the server
  // refuses with a conflict, so the screen says so rather than inviting a
  // submission that will fail.
  if (record?.annulment) {
    return (
      <div className="mx-auto w-full max-w-3xl space-y-6">
        <PageHeader title="Record an outcome" description={`For the registration under ${brn}.`} />
        <Alert variant="destructive">
          <ShieldOff />
          <AlertTitle>This registration is annulled</AlertTitle>
          <AlertDescription>
            There is no birth to attach an outcome to. An annulment says the event never happened.
          </AlertDescription>
        </Alert>
      </div>
    )
  }

  // Every message the server returned, whatever field it keyed them to —
  // nothing hidden behind a field the form does not surface.
  const allMessages = error ? [...new Set(error.fields.map((item) => item.message))] : []

  return (
    <div className="mx-auto w-full max-w-3xl space-y-6">
      <PageHeader
        title="Record an outcome"
        description={`A death following the birth recorded under ${brn}.`}
      />

      {allMessages.length > 0 ? (
        <Alert variant="destructive">
          <CircleAlert />
          <AlertTitle>Could not record the outcome</AlertTitle>
          <AlertDescription>
            <ul className="list-disc ps-4">
              {allMessages.map((message) => (
                <li key={message}>{message}</li>
              ))}
            </ul>
          </AlertDescription>
        </Alert>
      ) : null}

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Activity className="size-4" /> Outcome
          </CardTitle>
          <CardDescription>
            A live birth followed by a death is always two records. The cause of death is coded, not
            free text.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={submit}>
            <FieldGroup>
              <Field>
                <FieldLabel htmlFor="outcome-kind">Type of outcome</FieldLabel>
                <Select value={kind} onValueChange={(value) => setKind(value as Kind)}>
                  <SelectTrigger id="outcome-kind" className="w-72">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="neonatal">Neonatal death (child, ICD-PM)</SelectItem>
                    <SelectItem value="maternal">Maternal death (mother, ICD-MM)</SelectItem>
                  </SelectContent>
                </Select>
                <FieldDescription>
                  {kind === 'neonatal'
                    ? 'Death of the child within 28 days of a live birth.'
                    : 'Death of the mother linked to this birth event.'}
                </FieldDescription>
              </Field>

              <Field>
                <FieldLabel htmlFor="death-date">Date of death</FieldLabel>
                <Input
                  id="death-date"
                  type="date"
                  value={deathDate}
                  onChange={(event) => setDeathDate(event.target.value)}
                  required
                />
              </Field>

              {kind === 'neonatal' ? (
                <Field>
                  <FieldLabel htmlFor="timing">ICD-PM timing</FieldLabel>
                  <Select value={timing} onValueChange={(value) => setTiming(value as IcdPmTiming)}>
                    <SelectTrigger id="timing" className="w-72">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="Neonatal">Neonatal</SelectItem>
                      <SelectItem value="Antepartum">Antepartum</SelectItem>
                      <SelectItem value="Intrapartum">Intrapartum</SelectItem>
                    </SelectContent>
                  </Select>
                  <FieldDescription>
                    A live birth that later dies is Neonatal by definition; Antepartum and
                    Intrapartum are stillbirth categories.
                  </FieldDescription>
                </Field>
              ) : null}

              <Field>
                <FieldLabel htmlFor="cause">
                  {kind === 'neonatal' ? 'ICD-PM cause-of-death code' : 'ICD-MM cause-of-death code'}
                </FieldLabel>
                <Input
                  id="cause"
                  value={causeCode}
                  onChange={(event) => setCauseCode(event.target.value)}
                  placeholder={kind === 'neonatal' ? 'e.g. P21.0' : 'e.g. O72.1'}
                  required
                />
              </Field>

              {kind === 'neonatal' ? (
                <Field>
                  <FieldLabel htmlFor="maternal-condition">
                    Contributing maternal condition code (optional)
                  </FieldLabel>
                  <Input
                    id="maternal-condition"
                    value={maternalCondition}
                    onChange={(event) => setMaternalCondition(event.target.value)}
                    placeholder="ICD-PM requires this alongside the cause where known"
                  />
                </Field>
              ) : null}

              <Field orientation="horizontal">
                <Button type="submit" disabled={submitting || !deathDate || !causeCode}>
                  {submitting ? <Spinner /> : null}
                  Record {kind === 'neonatal' ? 'neonatal' : 'maternal'} death
                </Button>
                <Button asChild variant="ghost" type="button">
                  <Link to={`/records?brn=${encodeURIComponent(brn)}`}>Cancel</Link>
                </Button>
              </Field>
            </FieldGroup>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}
