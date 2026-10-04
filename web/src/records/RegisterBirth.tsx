import { useCallback, useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { type FieldErrors, useForm, useWatch } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { CircleAlert, Save, TriangleAlert } from 'lucide-react'
import { WebChannelDeviceId } from '@/auth/channel'
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
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'
import { PageHeader } from '@/shell/PageHeader'
import { type BrnDraw, createBrnDraw } from './brnDraw'
import {
  MarriageSection,
  ParentSection,
  PlaceOfBirthFields,
  ProofOfAddressSection,
  ServerErrors,
  parentForRequest,
} from './RegistrationSections'
import {
  type ParentInput,
  type RegistrationInput,
  daysSinceBirth,
  registrationSchema,
  today,
} from './registrationSchema'

type Facility = components['schemas']['FacilityResponse']

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

  // The draw above outlives any one render, but each number it asks for must
  // go through the session as it is *now*. The API client is rebuilt when the
  // token renews; a draw that captured the first render's drawBrn would keep
  // presenting the old token, and a registrar who corrected a refused form
  // after a renewal would have the next draw refused as unauthorised.
  const drawBrnRef = useRef<((facilityId: string) => Promise<string | null>) | null>(null)

  /**
   * What is still stopping the form from being submitted.
   *
   * Empty until a submission is actually refused, so the screen never scolds
   * someone for a field they have not reached yet.
   */
  const [blocking, setBlocking] = useState<string[]>([])
  const blockingRef = useRef<HTMLDivElement | null>(null)

  /**
   * The registry's own refusal, which renders at the top of the page.
   *
   * The submit button sits about 1800px down a form this long, on a 720px
   * viewport — so an error rendered at the top is roughly a thousand pixels
   * above where the person clicked, and nothing moves them to it. From the
   * button, "the registry refused this" and "the button is broken" look
   * identical.
   */
  const failureRef = useRef<HTMLDivElement | null>(null)

  const form = useForm<RegistrationInput>({
    // Every field named, including the ones behind a Select. An absent key
    // leaves the value undefined, which makes the control uncontrolled on the
    // first render and controlled on the next -- React warns about exactly
    // that, and a component switching modes can drop what was chosen.
    defaultValues: {
      facilityId: '',
      childGivenNames: '',
      childSurname: '',
      dateOfBirth: '',
      placeOfBirthKind: undefined,
      placeOfBirth: '',
      sex: undefined,
      plurality: undefined,
      birthWeightGrams: '',
      gestationalAgeWeeks: '',
      birthOrder: '',
      mother: blankParent(),
      father: blankParent(),
      marriage: { date: '', certificateNumber: '' },
      proofOfAddress: { kind: '', reference: '' },
    },
    resolver: zodResolver(registrationSchema),
  })

  // useWatch rather than form.watch: the hook subscribes to just these fields
  // and is safe under the React Compiler, which cannot memoise watch().
  const dateOfBirth = useWatch({ control: form.control, name: 'dateOfBirth' })
  const facilityId = useWatch({ control: form.control, name: 'facilityId' })
  const sex = useWatch({ control: form.control, name: 'sex' })
  const plurality = useWatch({ control: form.control, name: 'plurality' })

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

  /**
   * A refused submission has to say so **at the button**.
   *
   * Without this the click did nothing visible: the only feedback was a line
   * of red under whichever field was unfilled, which on a form this long is
   * usually scrolled off the screen. A registrar clicks Register, the page
   * does not move, and there is nothing to read — so they click again, and
   * conclude the system is broken.
   *
   * Focusing the offending field is not enough on its own here. Facility, sex
   * and plurality are set through `setValue` rather than `register`, so
   * react-hook-form has no input to move the cursor to for three of the five
   * required fields.
   */
  const refused = useCallback((errors: FieldErrors<RegistrationInput>) => {
    setBlocking(Object.keys(errors).map(label))
    show(blockingRef)
  }, [])

  const submit = useCallback(
    async (values: RegistrationInput) => {
      setBlocking([])
      setSubmitting(true)
      setError(null)

      try {
        drawnRef.current ??= createBrnDraw((facilityId) => {
          // Set by the effect after the first render, which is before any submit.
          const draw = drawBrnRef.current
          return draw ? draw(facilityId) : Promise.resolve(null)
        })

        const brn = await drawnRef.current.forAttempt(values.facilityId)

        if (!brn) {
          // drawBrn has already set the error -- a facility out of numbers, or
          // one this registrar may not act for. Same hole as below: without
          // this the refusal renders far above the button and the click looks
          // like it did nothing.
          show(failureRef)
          return
        }

        const { data, error: failure, response } = await api.POST('/api/BirthRecords/register', {
          body: {
            data: {
              brn,
              facilityId: values.facilityId,
              childGivenNames: values.childGivenNames.trim(),
              childSurname: values.childSurname.trim(),
              dateOfBirth: `${values.dateOfBirth}T00:00:00Z`,
              placeOfBirthKind: values.placeOfBirthKind,
              // The description belongs to a birth elsewhere; one typed before
              // switching back to "this facility" is not sent.
              placeOfBirth:
                values.placeOfBirthKind === 'ThisFacility' ? undefined : blankToUndefined(values.placeOfBirth),
              sex: values.sex,
              plurality: values.plurality,
              birthWeightGrams: optional(values.birthWeightGrams),
              gestationalAgeWeeks: optional(values.gestationalAgeWeeks),
              birthOrder: optional(values.birthOrder),
              mother: parentForRequest(values.mother),
              father: parentForRequest(values.father),
              marriage: partsOrNothing({
                date: blankToUndefined(values.marriage?.date),
                certificateNumber: blankToUndefined(values.marriage?.certificateNumber),
              }),
              proofOfAddress: partsOrNothing({
                kind: blankToUndefined(values.proofOfAddress?.kind),
                reference: blankToUndefined(values.proofOfAddress?.reference),
              }),
              deviceId: WebChannelDeviceId,
              // Sent only when the birth is outside the window. Supplying it
              // for an on-time birth is refused rather than ignored, because
              // it means the form and the registry disagree about the date.
              lateRegistration: isLate ? values.lateRegistration : undefined,
            },
          },
        })

        if (failure || !response.ok) {
          const refusal = toNcbrsError(failure, response.status)

          // When the registry objects to the *number*, the held one can never
          // work -- pressing Register again with it would refuse forever. Any
          // other objection is about the form, and keeping the number is what
          // stops a registrar fixing three fields and burning three BRNs.
          if (aboutTheNumber(refusal)) {
            drawnRef.current.discard()
          }

          setError(refusal)
          show(failureRef)
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
        body: { data: { blockSize: 1, deviceId: WebChannelDeviceId } },
      },
    )

    if (failure || !response.ok) {
      setError(toNcbrsError(failure, response.status))
      return null
    }

    // The number as the registry writes it. For a facility with an office
    // code that is composed (SS-JTH-2026-000123-K), and composing it here
    // would be a second copy of the format to drift from the registry's.
    // blockStart is the number itself only from a registry older than that.
    const granted = data?.data
    if (granted?.firstBrn) {
      return granted.firstBrn
    }

    const start = granted?.blockStart

    return start === undefined || start === null ? null : String(start)
  }

  // After drawBrn is declared: the draw kept in drawnRef calls through this.
  useEffect(() => {
    drawBrnRef.current = drawBrn
  })

  const chosen = facilities?.find((facility) => facility.facilityId === facilityId)

  return (
    <div className="mx-auto w-full max-w-5xl space-y-6">
      <PageHeader
        title="Register a birth"
        description="For a birth being filed at the centre. A facility device registers its own, from the block it already holds."
      />

      <div ref={failureRef}>{error ? <Failure error={error} /> : null}</div>

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
          {/* handleSubmit is called inside the handler rather than during render:
              the handlers it wraps read refs, and building it in render reads as
              touching them while rendering. Behaviour is identical. */}
          <form onSubmit={(event) => void form.handleSubmit(submit, refused)(event)} noValidate>
            <FieldGroup>
              {/* Two columns where there is room, paired so each row is one
                  question a registrar asks together: where and when, the
                  name, sex and plurality, the place. One column on a phone. */}
              <div className="grid items-start gap-5 md:grid-cols-2">
                <Field>
                  <FieldLabel htmlFor="facilityId">Facility</FieldLabel>
                  <Select
                    value={facilityId}
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
                  <FieldLabel htmlFor="childGivenNames">Child’s given names</FieldLabel>
                  <Input id="childGivenNames" {...form.register('childGivenNames')} autoComplete="off" />
                  <FieldError errors={[form.formState.errors.childGivenNames]} />
                  <ServerErrors error={error} field="data.childGivenNames" />
                </Field>

                <Field>
                  <FieldLabel htmlFor="childSurname">Child’s surname</FieldLabel>
                  <Input id="childSurname" {...form.register('childSurname')} autoComplete="off" />
                  <FieldDescription>Usually the father’s name.</FieldDescription>
                  <FieldError errors={[form.formState.errors.childSurname]} />
                  <ServerErrors error={error} field="data.childSurname" />
                </Field>

                <Field>
                  <FieldLabel htmlFor="sex">Sex</FieldLabel>
                  <Select
                    value={sex ?? ''}
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
                    value={plurality ?? ''}
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

                <PlaceOfBirthFields form={form} error={error} />
              </div>

              <FieldSet>
                <FieldLegend>Clinical measurements</FieldLegend>
                <FieldDescription>
                  Leave blank where nothing was measured. A blank is recorded as “not measured”,
                  which is a different fact from zero.
                </FieldDescription>
                <FieldGroup className="grid items-start gap-5 md:grid-cols-3">
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

              <ParentSection form={form} error={error} who="mother" />
              <ParentSection form={form} error={error} who="father" />
              <MarriageSection form={form} error={error} />
              <ProofOfAddressSection form={form} />

              {isLate ? <LateRegistrationFields form={form} error={error} /> : null}

              <div ref={blockingRef}>
                {blocking.length > 0 ? (
                  <Alert variant="destructive">
                    <CircleAlert />
                    <AlertTitle>This birth has not been registered yet</AlertTitle>
                    <AlertDescription>
                      Nothing was sent to the registry. Still needed:{' '}
                      {blocking.join(', ')}.
                    </AlertDescription>
                  </Alert>
                ) : null}
              </div>

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
  const evidenceType = useWatch({ control: form.control, name: 'lateRegistration.evidenceType' })

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
      <FieldGroup className="grid items-start gap-5 md:grid-cols-2">
        <Field>
          <FieldLabel htmlFor="evidenceType">Supporting evidence</FieldLabel>
          <Select
            value={evidenceType ?? ''}
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

/**
 * Brings a message the submission produced into view.
 *
 * After paint, because the element does not exist until the state that
 * renders it has been applied. Guarded, because scrollIntoView with options
 * is absent in jsdom and a test must not fail on the scroll rather than on
 * the behaviour.
 */
function show(target: React.RefObject<HTMLDivElement | null>) {
  requestAnimationFrame(() => target.current?.scrollIntoView?.({ block: "center", behavior: "smooth" }))
}

/**
 * Whether the registry refused the BRN rather than the form.
 *
 * Read from the field the server names, not from the status code. 409 also
 * covers a facility whose range is exhausted, where drawing again is precisely
 * the wrong move — the next number is no more available than the last.
 */
function aboutTheNumber(error: NcbrsError): boolean {
  return error.fields.some((item) => item.field.toLowerCase() === 'data.brn')
}

/**
 * The name a registrar sees on screen, not the name the schema uses.
 *
 * "childFullName" in a summary asks the reader to translate before they can
 * act, which is the opposite of what a message on a refused submission is for.
 */
function label(field: string): string {
  const names: Record<string, string> = {
    facilityId: 'facility',
    childGivenNames: 'the child’s given names',
    childSurname: 'the child’s surname',
    dateOfBirth: 'date of birth',
    placeOfBirthKind: 'place of birth',
    placeOfBirth: 'where the birth happened',
    sex: 'sex',
    plurality: 'plurality',
    birthWeightGrams: 'birth weight',
    gestationalAgeWeeks: 'gestational age',
    birthOrder: 'birth order',
    mother: 'the mother’s details',
    father: 'the father’s details',
    marriage: 'the marriage',
    proofOfAddress: 'the proof of address',
    lateRegistration: 'the late-registration evidence',
  }

  return names[field] ?? field
}

function blankToUndefined(value: string | undefined): string | undefined {
  return value === undefined || value.trim() === '' ? undefined : value.trim()
}

/** An optional group sent only when something in it was given. */
function partsOrNothing<T extends Record<string, string | undefined>>(parts: T): T | undefined {
  return Object.values(parts).some((value) => value !== undefined) ? parts : undefined
}

/**
 * Every key named, for the same reason as the form's other defaults: a key
 * absent at first render leaves its input uncontrolled until it is not.
 */
function blankParent(): ParentInput {
  return {
    givenNames: '',
    surname: '',
    maidenSurname: '',
    dateOfBirth: '',
    placeOfBirth: '',
    occupation: '',
    address: '',
    documentType: '',
    documentNumber: '',
  }
}
