import { useCallback, useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { CircleAlert, Save, TriangleAlert } from 'lucide-react'
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
import { Spinner } from '@/components/ui/spinner'
import type { components } from '@/api/generated/api'
import { type NcbrsError, messagesFor, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'
import { PageHeader } from '@/shell/PageHeader'
import { type BrnDraw, createBrnDraw } from './brnDraw'
import {
  type RegistrationInput,
  daysSinceBirth,
  registrationSchema,
  today,
} from './registrationSchema'

type Facility = components['schemas']['FacilityResponse']

/** Identifies the central web app in the audit trail, matching X-Client-Id. */
const WebClientDeviceId = 'ncbrs-web'

/**
 * Registering a birth from the central web app.
 *
 * **A browser is not a facility device**, and the difference decides how this
 * works. A device holds a block of BRNs granted in advance precisely so it can
 * register with no connectivity; a browser has neither block nor enrolment. So
 * rather than inventing a number, this draws one — a block of exactly one —
 * from the facility's own pre-approved range, through the same endpoint and the
 * same `[ConcurrencyCheck]` counter that grants device blocks.
 *
 * That keeps design decision #2 intact rather than working around it: the
 * number comes from the facility's range, `BrnBlockNextAvailable` advances
 * atomically, and it can never collide with a block granted to a device next
 * month.
 */
export function RegisterBirth() {
  const api = useApiClient()
  const navigate = useNavigate()

  const [facilities, setFacilities] = useState<Facility[] | null>(null)
  const [windowDays, setWindowDays] = useState<number | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [submitting, setSubmitting] = useState(false)

  // Holds the number across retries so a refused submission does not burn a
  // fresh one each attempt. The rule, and why it matters, lives in brnDraw.ts.
  const drawnRef = useRef<BrnDraw | null>(null)

  const form = useForm<RegistrationInput>({
    resolver: zodResolver(registrationSchema),
    defaultValues: {
      facilityId: '',
      childFullName: '',
      dateOfBirth: '',
      motherFullName: '',
      fatherFullName: '',
    },
  })

  const dateOfBirth = form.watch('dateOfBirth')

  // Whether to ask for evidence at all. The server decides bindingly against
  // the device's capture time; this is the form trying to ask the right
  // questions before it gets there, using the window the server published
  // rather than a number copied into the client.
  const isLate = windowDays !== null && dateOfBirth !== '' && daysSinceBirth(dateOfBirth) > windowDays

  useEffect(() => {
    let cancelled = false

    async function load() {
      try {
        const [facilityPage, rules] = await Promise.all([
          api.GET('/api/facilities', { params: { query: { limit: 200 } } }),
          api.GET('/api/BirthRecords/registration-rules', {}),
        ])

        if (cancelled) {
          return
        }

        setFacilities(facilityPage.data?.data?.items ?? [])
        setWindowDays(rules.data?.data?.statutoryWindowDays ?? null)
      } catch (cause) {
        if (!cancelled) {
          setError(unreachableError(cause))
          setFacilities([])
        }
      }
    }

    void load()

    return () => {
      cancelled = true
    }
  }, [api])

  const submit = useCallback(
    async (values: RegistrationInput) => {
      setSubmitting(true)
      setError(null)

      try {
        drawnRef.current ??= createBrnDraw(drawBrn)

        const brn = await drawnRef.current.forAttempt(values.facilityId)

        if (!brn) {
          return
        }

        const { data, error: failure, response } = await api.POST('/api/BirthRecords/register', {
          body: {
            data: {
              brn,
              facilityId: values.facilityId,
              childFullName: values.childFullName,
              dateOfBirth: `${values.dateOfBirth}T00:00:00Z`,
              sex: values.sex,
              plurality: values.plurality,
              birthWeightGrams: optional(values.birthWeightGrams),
              gestationalAgeWeeks: optional(values.gestationalAgeWeeks),
              birthOrder: optional(values.birthOrder),
              motherFullName: blankToUndefined(values.motherFullName),
              fatherFullName: blankToUndefined(values.fatherFullName),
              deviceId: WebClientDeviceId,
              // Sent only when the birth is outside the window. Supplying it
              // for an on-time birth is refused rather than ignored, because
              // it means the form and the registry disagree about the date.
              lateRegistration: isLate ? values.lateRegistration : undefined,
            },
          },
        })

        if (failure || !response.ok) {
          setError(toNcbrsError(failure, response.status))
          return
        }

        // Spent. A retry after this point would be a second registration, not
        // another attempt at this one, and must draw its own number.
        drawnRef.current.spend()

        const registered = data?.data
        void navigate(`/records?brn=${encodeURIComponent(registered?.brn ?? brn)}`)
      } catch (cause) {
        setError(unreachableError(cause))
      } finally {
        setSubmitting(false)
      }
    },
    [api, isLate, navigate],
  )

  async function drawBrn(facilityId: string): Promise<string | null> {
    const { data, error: failure, response } = await api.POST(
      '/api/BirthRecords/{facilityId}/request-brn-block',
      {
        params: { path: { facilityId } },
        // One number, not a block. The web app registers one birth at a time
        // and has no offline period to cover.
        body: { data: { blockSize: 1, deviceId: WebClientDeviceId } },
      },
    )

    if (failure || !response.ok) {
      setError(toNcbrsError(failure, response.status))
      return null
    }

    const start = data?.data?.blockStart

    return start === undefined || start === null ? null : String(start)
  }

  const chosen = facilities?.find((facility) => facility.facilityId === form.watch('facilityId'))

  return (
    <div className="mx-auto w-full max-w-3xl space-y-6">
      <PageHeader
        title="Register a birth"
        description="For a birth being filed at the centre. A facility device registers its own, from the block it already holds."
      />

      {error ? <Failure error={error} /> : null}

      {chosen && chosen.brnRemaining <= 0 ? (
        <Alert variant="destructive">
          <TriangleAlert />
          <AlertTitle>This facility has no registration numbers left</AlertTitle>
          <AlertDescription>
            {chosen.name} has used its entire pre-approved BRN range. The central registry must
            assign a new range before any more births can be registered there.
          </AlertDescription>
        </Alert>
      ) : null}

      <Card>
        <CardHeader>
          <CardTitle>Birth details</CardTitle>
          <CardDescription>
            The registration number is drawn from the facility’s own range when you save.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={form.handleSubmit(submit)} noValidate>
            <FieldGroup>
              <Field>
                <FieldLabel htmlFor="facilityId">Facility</FieldLabel>
                <Select
                  value={form.watch('facilityId')}
                  onValueChange={(value) =>
                    form.setValue('facilityId', value, { shouldValidate: true })
                  }
                >
                  <SelectTrigger id="facilityId">
                    <SelectValue placeholder={facilities === null ? 'Loading…' : 'Choose a facility'} />
                  </SelectTrigger>
                  <SelectContent>
                    {(facilities ?? []).map((facility) => (
                      <SelectItem key={facility.facilityId} value={facility.facilityId}>
                        {facility.name}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <FieldDescription>
                  Only facilities in your district. The birth is registered against this
                  facility’s range.
                </FieldDescription>
                <FieldError errors={[form.formState.errors.facilityId]} />
                <ServerErrors error={error} field="data.facilityId" />
              </Field>

              <Field>
                <FieldLabel htmlFor="childFullName">Child’s full name</FieldLabel>
                <Input id="childFullName" {...form.register('childFullName')} autoComplete="off" />
                <FieldError errors={[form.formState.errors.childFullName]} />
                <ServerErrors error={error} field="data.childFullName" />
              </Field>

              <Field>
                <FieldLabel htmlFor="dateOfBirth">Date of birth</FieldLabel>
                <Input
                  id="dateOfBirth"
                  type="date"
                  max={today()}
                  {...form.register('dateOfBirth')}
                />
                <FieldDescription>
                  {windowDays === null
                    ? 'The statutory window is being read from the registry.'
                    : `Births registered more than ${windowDays} days after they happened need supporting evidence.`}
                </FieldDescription>
                <FieldError errors={[form.formState.errors.dateOfBirth]} />
                <ServerErrors error={error} field="data.dateOfBirth" />
              </Field>

              <Field>
                <FieldLabel htmlFor="sex">Sex</FieldLabel>
                <Select
                  value={form.watch('sex')}
                  onValueChange={(value) =>
                    form.setValue('sex', value as RegistrationInput['sex'], {
                      shouldValidate: true,
                    })
                  }
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
                <FieldError errors={[form.formState.errors.sex]} />
              </Field>

              <Field>
                <FieldLabel htmlFor="plurality">Plurality</FieldLabel>
                <Select
                  value={form.watch('plurality')}
                  onValueChange={(value) =>
                    form.setValue('plurality', value as RegistrationInput['plurality'], {
                      shouldValidate: true,
                    })
                  }
                >
                  <SelectTrigger id="plurality">
                    <SelectValue placeholder="Choose" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="Singleton">Singleton</SelectItem>
                    <SelectItem value="Twin">Twin</SelectItem>
                    <SelectItem value="Triplet">Triplet</SelectItem>
                    <SelectItem value="HigherOrderMultiple">Higher-order multiple</SelectItem>
                  </SelectContent>
                </Select>
                <FieldError errors={[form.formState.errors.plurality]} />
              </Field>

              <FieldSet>
                <FieldLegend>Clinical measurements</FieldLegend>
                <FieldDescription>
                  Leave blank where nothing was measured. A blank is recorded as “not measured”,
                  which is a different fact from zero.
                </FieldDescription>
                <FieldGroup>
                  <Field>
                    <FieldLabel htmlFor="birthWeightGrams">Birth weight (grams)</FieldLabel>
                    <Input
                      id="birthWeightGrams"
                      type="number"
                      inputMode="numeric"
                      {...form.register('birthWeightGrams')}
                    />
                    <FieldError errors={[form.formState.errors.birthWeightGrams]} />
                    <ServerErrors error={error} field="data.birthWeightGrams" />
                  </Field>

                  <Field>
                    <FieldLabel htmlFor="gestationalAgeWeeks">Gestational age (weeks)</FieldLabel>
                    <Input
                      id="gestationalAgeWeeks"
                      type="number"
                      step="0.1"
                      {...form.register('gestationalAgeWeeks')}
                    />
                    <FieldError errors={[form.formState.errors.gestationalAgeWeeks]} />
                    <ServerErrors error={error} field="data.gestationalAgeWeeks" />
                  </Field>

                  <Field>
                    <FieldLabel htmlFor="birthOrder">Birth order</FieldLabel>
                    <Input
                      id="birthOrder"
                      type="number"
                      inputMode="numeric"
                      {...form.register('birthOrder')}
                    />
                    <FieldError errors={[form.formState.errors.birthOrder]} />
                    <ServerErrors error={error} field="data.birthOrder" />
                  </Field>
                </FieldGroup>
              </FieldSet>

              <FieldSet>
                <FieldLegend>Parents</FieldLegend>
                <FieldGroup>
                  <Field>
                    <FieldLabel htmlFor="motherFullName">Mother’s full name</FieldLabel>
                    <Input id="motherFullName" {...form.register('motherFullName')} autoComplete="off" />
                    <FieldError errors={[form.formState.errors.motherFullName]} />
                  </Field>

                  <Field>
                    <FieldLabel htmlFor="fatherFullName">Father’s full name</FieldLabel>
                    <Input id="fatherFullName" {...form.register('fatherFullName')} autoComplete="off" />
                    <FieldError errors={[form.formState.errors.fatherFullName]} />
                  </Field>
                </FieldGroup>
              </FieldSet>

              {isLate ? <LateRegistrationFields form={form} error={error} /> : null}

              <Button type="submit" disabled={submitting} className="w-fit">
                {submitting ? <Spinner /> : <Save />}
                Register the birth
              </Button>
            </FieldGroup>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}

/**
 * Only rendered when the birth falls outside the statutory window.
 *
 * Late registration is the ordinary route for a large share of rural births —
 * a child registered when they first reach school is the rule, not the
 * exception — so this has to read as the next step in a normal process rather
 * than as a warning about something having gone wrong. What it adds is a
 * second person checking evidence, and the honest thing to say is that the
 * certificate waits, not the registration.
 */
function LateRegistrationFields({
  form,
  error,
}: {
  form: ReturnType<typeof useForm<RegistrationInput>>
  error: NcbrsError | null
}) {
  return (
    <FieldSet>
      <FieldLegend>Late registration</FieldLegend>
      <Alert>
        <TriangleAlert />
        <AlertTitle>This birth is outside the statutory window</AlertTitle>
        <AlertDescription>
          The registration will be created and the child will have a registration number. The
          certificate waits until a district registrar other than you has verified this evidence.
        </AlertDescription>
      </Alert>
      <FieldGroup>
        <Field>
          <FieldLabel htmlFor="evidenceType">Supporting evidence</FieldLabel>
          <Select
            value={form.watch('lateRegistration.evidenceType')}
            onValueChange={(value) =>
              form.setValue(
                'lateRegistration.evidenceType',
                value as NonNullable<RegistrationInput['lateRegistration']>['evidenceType'],
                { shouldValidate: true },
              )
            }
          >
            <SelectTrigger id="evidenceType">
              <SelectValue placeholder="Choose" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="HealthFacilityRecord">Health facility record</SelectItem>
              <SelectItem value="AntenatalOrDeliveryCard">Antenatal or delivery card</SelectItem>
              <SelectItem value="ImmunisationRecord">Immunisation record</SelectItem>
              <SelectItem value="BirthAttendantAttestation">Birth attendant attestation</SelectItem>
              <SelectItem value="ReligiousRecord">Religious record</SelectItem>
              <SelectItem value="SchoolRecord">School record</SelectItem>
              <SelectItem value="SwornAffidavit">Sworn affidavit</SelectItem>
              <SelectItem value="CourtOrder">Court order</SelectItem>
            </SelectContent>
          </Select>
          <FieldDescription>
            Coded rather than written out, because which evidence late registrations rest on is
            itself a statistic the Ministry needs.
          </FieldDescription>
          <FieldError errors={[form.formState.errors.lateRegistration?.evidenceType]} />
        </Field>

        <Field>
          <FieldLabel htmlFor="evidenceReference">Evidence reference</FieldLabel>
          <Input
            id="evidenceReference"
            {...form.register('lateRegistration.evidenceReference')}
            autoComplete="off"
          />
          <FieldDescription>The card number, register entry or affidavit reference.</FieldDescription>
        </Field>

        <Field>
          <FieldLabel htmlFor="declarantName">Declarant</FieldLabel>
          <Input
            id="declarantName"
            {...form.register('lateRegistration.declarantName')}
            autoComplete="off"
          />
          <FieldDescription>Who is presenting the claim.</FieldDescription>
          <FieldError errors={[form.formState.errors.lateRegistration?.declarantName]} />
        </Field>

        <Field>
          <FieldLabel htmlFor="declarantRelationship">Their relationship to the child</FieldLabel>
          <Input
            id="declarantRelationship"
            placeholder="mother, father, guardian"
            {...form.register('lateRegistration.declarantRelationship')}
            autoComplete="off"
          />
          <FieldError errors={[form.formState.errors.lateRegistration?.declarantRelationship]} />
        </Field>

        <ServerErrors error={error} field="data.lateRegistration" />
      </FieldGroup>
    </FieldSet>
  )
}

/**
 * What the server said about this field.
 *
 * Kept alongside the client-side message rather than instead of it. The two
 * answer different questions — "this is not a date" versus "this facility has
 * exhausted its range" — and the server's is the one that decided.
 */
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
          ? 'The registry did not answer. Nothing was registered; try again when the connection is back.'
          : error.fields.map((item) => item.message).join(' ')}
      </AlertDescription>
    </Alert>
  )
}

/**
 * Blank stays blank all the way to the wire: "not measured", never zero.
 *
 * Takes `unknown` because these arrive as the form's *input* type, before zod
 * has coerced them — a text input holds a string until it does not. Anything
 * that is not a finite number becomes absent, which is the honest reading of
 * an unmeasured weight and the one thing that must not turn into 0.
 */
function optional(value: unknown): number | undefined {
  if (value === '' || value === undefined || value === null) {
    return undefined
  }

  const parsed = Number(value)

  return Number.isFinite(parsed) ? parsed : undefined
}

function blankToUndefined(value: string | undefined): string | undefined {
  return value === undefined || value.trim() === '' ? undefined : value
}
