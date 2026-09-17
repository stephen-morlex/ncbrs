import { useCallback, useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { CircleAlert, CircleCheck, Clock, PenLine, ShieldOff } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Field,
  FieldDescription,
  FieldError,
  FieldGroup,
  FieldLabel,
  FieldLegend,
  FieldSet,
} from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Spinner } from '@/components/ui/spinner'
import { Textarea } from '@/components/ui/textarea'
import type { components } from '@/api/generated/api'
import { type NcbrsError, messagesFor, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'
import { PageHeader } from '@/shell/PageHeader'
import { changedFields, withdrawsCertificate } from './correction'

type BirthRecord = components['schemas']['BirthRecordResponse']
type AmendResponse = components['schemas']['AmendBirthRecordResponse']

const WebClientDeviceId = 'ncbrs-web'

interface Draft extends Record<string, unknown> {
  childFullName: string
  dateOfBirth: string
  sex: string
  motherFullName: string
  fatherFullName: string
  birthWeightGrams: string
  gestationalAgeWeeks: string
  birthOrder: string
}

/**
 * Requesting a correction to a registered birth.
 *
 * **Corrections run on two tracks, and the screen says which before you
 * submit.** The clinical measurements — birth weight, gestational age, birth
 * order — describe the event and take effect at once. Everything describing
 * *who the record is about* waits for a reviewer who is not the submitter.
 *
 * Saying that afterwards would be too late. A registrar correcting a child's
 * name has a family in front of them, and needs to know before they press save
 * that the record will not change today.
 */
export function RequestCorrection() {
  const api = useApiClient()

  const [params] = useSearchParams()
  const brn = params.get('brn') ?? ''

  const [record, setRecord] = useState<BirthRecord | null>(null)
  const [original, setOriginal] = useState<Draft | null>(null)
  const [draft, setDraft] = useState<Draft | null>(null)
  const [reason, setReason] = useState('')
  const [error, setError] = useState<NcbrsError | null>(null)
  const [outcome, setOutcome] = useState<AmendResponse | null>(null)
  const [submitting, setSubmitting] = useState(false)

  useEffect(() => {
    let cancelled = false

    async function load() {
      if (!brn) {
        return
      }

      try {
        const { data, error: failure, response } = await api.GET('/api/BirthRecords/{brn}', {
          params: { path: { brn } },
        })

        if (cancelled) {
          return
        }

        if (failure || !response.ok) {
          setError(toNcbrsError(failure, response.status))
          return
        }

        const found = data?.data ?? null
        setRecord(found)

        // Prefilled with what the record says, so a correction is an edit of
        // the register rather than a blank form someone retypes from memory.
        const asDraft = toDraft(found)
        setOriginal(asDraft)
        setDraft(asDraft)
      } catch (cause) {
        if (!cancelled) {
          setError(unreachableError(cause))
        }
      }
    }

    void load()

    return () => {
      cancelled = true
    }
  }, [api, brn])

  const changes = original && draft ? changedFields(original, draft) : {}
  const changedNames = Object.keys(changes)

  const submit = useCallback(
    async (event: React.FormEvent) => {
      event.preventDefault()

      if (!draft || !original || changedNames.length === 0) {
        return
      }

      setSubmitting(true)
      setError(null)

      try {
        const { data, error: failure, response } = await api.PATCH('/api/BirthRecords/{brn}', {
          params: { path: { brn } },
          body: {
            data: {
              // Only what actually changed. Sending the whole form back would
              // file a correction for every untouched field, and drag each one
              // onto the approval track for nothing.
              ...toRequest(changes as Partial<Draft>),
              reason,
              deviceId: WebClientDeviceId,
            },
          },
        })

        if (failure || !response.ok) {
          setError(toNcbrsError(failure, response.status))
          return
        }

        // 200 and 202 are both successes and both land here. Which one it was
        // is carried by the payload's own split, not by the status code, so
        // the screen below never has to guess.
        setOutcome(data?.data ?? null)
      } catch (cause) {
        setError(unreachableError(cause))
      } finally {
        setSubmitting(false)
      }
    },
    [api, brn, changes, changedNames.length, draft, original, reason],
  )

  if (!brn) {
    return (
      <div className="mx-auto w-full max-w-3xl space-y-6">
        <PageHeader title="Request a correction" description="Open a record first." />
        <Alert variant="destructive">
          <CircleAlert />
          <AlertTitle>No record named</AlertTitle>
          <AlertDescription>
            A correction is made to a particular registration. Find the record first, then choose
            “Request a correction”.
          </AlertDescription>
        </Alert>
      </div>
    )
  }

  if (outcome) {
    return <Outcome outcome={outcome} brn={brn} />
  }

  return (
    <div className="mx-auto w-full max-w-3xl space-y-6">
      <PageHeader
        title="Request a correction"
        description={`Correcting the registration recorded under ${brn}.`}
      />

      {error && messagesFor(error, 'data').length === 0 ? <Failure error={error} /> : null}

      {!draft ? (
        <Card>
          <CardContent className="space-y-3 pt-6">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </CardContent>
        </Card>
      ) : (
        <form onSubmit={submit} noValidate>
          <div className="space-y-6">
            <Card>
              <CardHeader>
                <CardTitle>Takes effect immediately</CardTitle>
                <CardDescription>
                  These describe the birth itself. A corrected measurement is simply a better
                  measurement, so no second signature is needed.
                </CardDescription>
              </CardHeader>
              <CardContent>
                <FieldGroup>
                  <Field>
                    <FieldLabel htmlFor="birthWeightGrams">Birth weight (grams)</FieldLabel>
                    <Input
                      id="birthWeightGrams"
                      type="number"
                      value={draft.birthWeightGrams}
                      onChange={(event) => set(setDraft, 'birthWeightGrams', event.target.value)}
                    />
                    <ServerErrors error={error} field="data.birthWeightGrams" />
                  </Field>

                  <Field>
                    <FieldLabel htmlFor="gestationalAgeWeeks">Gestational age (weeks)</FieldLabel>
                    <Input
                      id="gestationalAgeWeeks"
                      type="number"
                      step="0.1"
                      value={draft.gestationalAgeWeeks}
                      onChange={(event) => set(setDraft, 'gestationalAgeWeeks', event.target.value)}
                    />
                    <ServerErrors error={error} field="data.gestationalAgeWeeks" />
                  </Field>

                  <Field>
                    <FieldLabel htmlFor="birthOrder">Birth order</FieldLabel>
                    <Input
                      id="birthOrder"
                      type="number"
                      value={draft.birthOrder}
                      onChange={(event) => set(setDraft, 'birthOrder', event.target.value)}
                    />
                    <ServerErrors error={error} field="data.birthOrder" />
                  </Field>
                </FieldGroup>
              </CardContent>
            </Card>

            <Card>
              <CardHeader>
                <CardTitle>Needs a second registrar</CardTitle>
                <CardDescription>
                  These say who the record is about. A district registrar other than you must agree
                  before the register changes — until then it reads exactly as it does now.
                </CardDescription>
              </CardHeader>
              <CardContent>
                <FieldGroup>
                  <Field>
                    <FieldLabel htmlFor="childFullName">Child’s full name</FieldLabel>
                    <Input
                      id="childFullName"
                      value={draft.childFullName}
                      onChange={(event) => set(setDraft, 'childFullName', event.target.value)}
                    />
                    <CertificateWarning record={record} field="childFullName" changes={changes} />
                    <ServerErrors error={error} field="data.childFullName" />
                  </Field>

                  <Field>
                    <FieldLabel htmlFor="dateOfBirth">Date of birth</FieldLabel>
                    <Input
                      id="dateOfBirth"
                      type="date"
                      value={draft.dateOfBirth}
                      onChange={(event) => set(setDraft, 'dateOfBirth', event.target.value)}
                    />
                    <CertificateWarning record={record} field="dateOfBirth" changes={changes} />
                    <ServerErrors error={error} field="data.dateOfBirth" />
                  </Field>

                  <Field>
                    <FieldLabel htmlFor="sex">Sex</FieldLabel>
                    <Select
                      value={draft.sex}
                      onValueChange={(value) => set(setDraft, 'sex', value)}
                    >
                      <SelectTrigger id="sex">
                        <SelectValue placeholder="Choose" />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value="Female">Female</SelectItem>
                        <SelectItem value="Male">Male</SelectItem>
                        <SelectItem value="Undetermined">Undetermined</SelectItem>
                      </SelectContent>
                    </Select>
                    <CertificateWarning record={record} field="sex" changes={changes} />
                  </Field>

                  <FieldSet>
                    <FieldLegend>Parents</FieldLegend>
                    <FieldDescription>
                      No certificate is withdrawn by correcting these, but filiation changes — which
                      is why they still wait for a reviewer.
                    </FieldDescription>
                    <FieldGroup>
                      <Field>
                        <FieldLabel htmlFor="motherFullName">Mother’s full name</FieldLabel>
                        <Input
                          id="motherFullName"
                          value={draft.motherFullName}
                          onChange={(event) => set(setDraft, 'motherFullName', event.target.value)}
                        />
                      </Field>

                      <Field>
                        <FieldLabel htmlFor="fatherFullName">Father’s full name</FieldLabel>
                        <Input
                          id="fatherFullName"
                          value={draft.fatherFullName}
                          onChange={(event) => set(setDraft, 'fatherFullName', event.target.value)}
                        />
                      </Field>
                    </FieldGroup>
                  </FieldSet>
                </FieldGroup>
              </CardContent>
            </Card>

            <Card>
              <CardContent className="pt-6">
                <FieldGroup>
                  <Field>
                    <FieldLabel htmlFor="reason">Why is this being corrected?</FieldLabel>
                    <Textarea
                      id="reason"
                      value={reason}
                      onChange={(event) => setReason(event.target.value)}
                      rows={3}
                    />
                    <FieldDescription>
                      Kept with the change forever, and read by whoever reviews it. “Typo” tells a
                      reviewer nothing they can act on.
                    </FieldDescription>
                    {reason.trim() === '' && changedNames.length > 0 ? (
                      <FieldError>A reason is required.</FieldError>
                    ) : null}
                    <ServerErrors error={error} field="data.reason" />
                  </Field>

                  {changedNames.length === 0 ? (
                    <FieldDescription>
                      Nothing has been changed yet. A correction names only what was wrong.
                    </FieldDescription>
                  ) : (
                    <FieldDescription>
                      Correcting {changedNames.length}{' '}
                      {changedNames.length === 1 ? 'field' : 'fields'}.
                    </FieldDescription>
                  )}

                  <ServerErrors error={error} field="data" />

                  <Button
                    type="submit"
                    className="w-fit"
                    disabled={submitting || changedNames.length === 0 || reason.trim() === ''}
                  >
                    {submitting ? <Spinner /> : <PenLine />}
                    Submit the correction
                  </Button>
                </FieldGroup>
              </CardContent>
            </Card>
          </div>
        </form>
      )}
    </div>
  )
}

/**
 * What happened, told as two separate facts.
 *
 * **A pending correction is not a failure and must not read as one** — the API
 * answers 202 rather than 200 precisely because something real happened and
 * nothing has changed yet. But it is also not a success in the way a registrar
 * would report to a family, and a single green tick would be read as one.
 */
function Outcome({ outcome, brn }: { outcome: AmendResponse; brn: string }) {
  const applied = outcome.applied ?? []
  const pending = outcome.pendingApproval ?? []

  return (
    <div className="mx-auto w-full max-w-3xl space-y-6">
      <PageHeader title="Correction submitted" description={`Registration ${brn}.`} />

      {applied.length > 0 ? (
        <Alert>
          <CircleCheck />
          <AlertTitle>
            {applied.length} {applied.length === 1 ? 'change is' : 'changes are'} already in effect
          </AlertTitle>
          <AlertDescription>
            <ChangeList changes={applied} />
          </AlertDescription>
        </Alert>
      ) : null}

      {pending.length > 0 ? (
        <Alert>
          <Clock />
          <AlertTitle>
            {pending.length} {pending.length === 1 ? 'change is' : 'changes are'} waiting for a
            reviewer
          </AlertTitle>
          <AlertDescription className="space-y-2">
            <p>
              <strong>The record has not changed.</strong> It still reads as it did, and will until
              a district registrar other than you approves this. Do not tell the family the
              correction has been made.
            </p>
            <ChangeList changes={pending} />
          </AlertDescription>
        </Alert>
      ) : null}

      {outcome.certificateInvalidated ? (
        <Alert variant="destructive">
          <ShieldOff />
          <AlertTitle>The certificate has been withdrawn</AlertTitle>
          <AlertDescription>
            A change that took effect touches what the certificate’s signature covers, so the
            document already issued no longer describes the record. Any copy in circulation will
            fail verification and a replacement must be issued.
          </AlertDescription>
        </Alert>
      ) : null}

      <Button asChild variant="outline" className="w-fit">
        <Link to={`/records?brn=${encodeURIComponent(brn)}`}>Back to the record</Link>
      </Button>
    </div>
  )
}

function ChangeList({ changes }: { changes: components['schemas']['AmendedFieldResponse'][] }) {
  return (
    <ul className="mt-1 space-y-1">
      {changes.map((change) => (
        <li key={change.field} className="text-sm">
          <span className="font-medium">{change.field}</span>
          {': '}
          {/* The real previous value, from the register rather than the form --
              an audit trail asserting a transition that never happened is worse
              than no trail. */}
          <span className="text-muted-foreground">{change.previousValue ?? '—'}</span>
          {' → '}
          <span>{change.newValue ?? '—'}</span>
        </li>
      ))}
    </ul>
  )
}

/**
 * Warns only when a certificate exists *and* the field being changed is one its
 * signature covers.
 *
 * Both halves matter. Warning on every name edit would cry wolf on records that
 * were never certificated; warning on a parent's name would claim a withdrawal
 * that does not happen.
 */
function CertificateWarning({
  record,
  field,
  changes,
}: {
  record: BirthRecord | null
  field: string
  changes: Record<string, unknown>
}) {
  if (!record?.certificate?.isValid || !withdrawsCertificate(field)) {
    return null
  }

  if (!(field in changes)) {
    return null
  }

  return (
    <FieldDescription className="text-destructive">
      If approved, this withdraws the certificate issued on this record.
    </FieldDescription>
  )
}

function ServerErrors({ error, field }: { error: NcbrsError | null; field: string }) {
  const messages = messagesFor(error, field)

  if (messages.length === 0) {
    return null
  }

  return <FieldError>{messages.join(' ')}</FieldError>
}

function Failure({ error }: { error: NcbrsError }) {
  return (
    <Alert variant="destructive">
      <CircleAlert />
      <AlertTitle>{error.title}</AlertTitle>
      <AlertDescription>
        {error.unreachable
          ? 'The registry did not answer. Nothing was submitted.'
          : error.fields.map((item) => item.message).join(' ')}
      </AlertDescription>
    </Alert>
  )
}

function set(
  setDraft: React.Dispatch<React.SetStateAction<Draft | null>>,
  field: keyof Draft,
  value: string,
) {
  setDraft((current) => (current ? { ...current, [field]: value } : current))
}

/**
 * What the register currently says, as form values.
 *
 * Null becomes an empty string rather than "0" or "null": a birth weight that
 * was never measured must render as an empty box, not as a number somebody
 * might leave in place and thereby assert.
 */
function toDraft(record: BirthRecord | null): Draft {
  return {
    childFullName: record?.childFullName ?? '',
    dateOfBirth: (record?.dateOfBirth ?? '').split('T')[0] ?? '',
    sex: record?.sex ?? '',
    motherFullName: record?.motherFullName ?? '',
    fatherFullName: record?.fatherFullName ?? '',
    birthWeightGrams: asText(record?.birthWeightGrams),
    gestationalAgeWeeks: asText(record?.gestationalAgeWeeks),
    birthOrder: asText(record?.birthOrder),
  }
}

function asText(value: number | null | undefined): string {
  return value === null || value === undefined ? '' : String(value)
}

/**
 * Turns the changed form values into the request's own types.
 *
 * Blank stays absent rather than becoming zero, for the same reason it does at
 * registration: an unmeasured weight is a different fact from a 0g baby.
 */
function toRequest(changes: Partial<Draft>): Record<string, unknown> {
  const request: Record<string, unknown> = {}

  for (const [field, value] of Object.entries(changes)) {
    if (field === 'dateOfBirth') {
      request[field] = value === '' ? undefined : `${value}T00:00:00Z`
    } else if (
      field === 'birthWeightGrams' ||
      field === 'gestationalAgeWeeks' ||
      field === 'birthOrder'
    ) {
      request[field] = value === '' || value === undefined ? undefined : Number(value)
    } else {
      request[field] = value
    }
  }

  return request
}
