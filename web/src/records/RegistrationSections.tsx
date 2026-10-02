import { type ReactNode, useState } from 'react'
import { type UseFormReturn, useWatch } from 'react-hook-form'
import { ChevronDown } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible'
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
import type { components } from '@/api/generated/api'
import { type NcbrsError, messagesFor } from '@/api/errors'
import { type RegistrationInput, documentTypes, placesOfBirth, today } from './registrationSchema'

type Form = UseFormReturn<RegistrationInput>
type ParentDetails = components['schemas']['ParentDetails']

export const PlaceLabels: Record<(typeof placesOfBirth)[number], string> = {
  ThisFacility: 'At this facility',
  OtherHealthFacility: 'At another health facility',
  Home: 'At home',
  Elsewhere: 'Elsewhere (on the way, in the community)',
}

export const DocumentLabels: Record<(typeof documentTypes)[number], string> = {
  Passport: 'Passport',
  BirthCertificate: 'Birth certificate',
  DrivingLicence: 'Driving licence',
  NationalId: 'National ID',
}

/** Where the birth happened. Required: the place is part of the legal record. */
export function PlaceOfBirthFields({ form, error }: { form: Form; error: NcbrsError | null }) {
  const kind = useWatch({ control: form.control, name: 'placeOfBirthKind' })

  return (
    <>
      <Field>
        <FieldLabel htmlFor="placeOfBirthKind">Place of birth</FieldLabel>
        <Select
          value={kind ?? ''}
          onValueChange={(value) =>
            form.setValue('placeOfBirthKind', value as RegistrationInput['placeOfBirthKind'], {
              shouldValidate: true,
            })
          }
        >
          <SelectTrigger id="placeOfBirthKind">
            <SelectValue placeholder="Choose" />
          </SelectTrigger>
          <SelectContent>
            {placesOfBirth.map((place) => (
              <SelectItem key={place} value={place}>
                {PlaceLabels[place]}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <FieldError errors={[form.formState.errors.placeOfBirthKind]} />
        <ServerErrors error={error} field="data.placeOfBirthKind" />
      </Field>

      {kind && kind !== 'ThisFacility' ? (
        <Field>
          <FieldLabel htmlFor="placeOfBirth">Where</FieldLabel>
          <Input
            id="placeOfBirth"
            placeholder="The facility, village or place"
            {...form.register('placeOfBirth')}
            autoComplete="off"
          />
          <FieldError errors={[form.formState.errors.placeOfBirth]} />
          <ServerErrors error={error} field="data.placeOfBirth" />
        </Field>
      ) : null}
    </>
  )
}

/**
 * An optional part of the registration, closed until wanted.
 *
 * Opened by itself when something in it is refused: a collapsed section with
 * an error inside reads as a form that will not submit for no reason.
 */
function Section({
  title,
  description,
  hasErrors,
  children,
}: {
  title: string
  description: string
  hasErrors: boolean
  children: ReactNode
}) {
  const [open, setOpen] = useState(false)
  const shown = open || hasErrors

  return (
    <Collapsible open={shown} onOpenChange={setOpen} asChild>
      <FieldSet>
        <div className="flex items-center justify-between gap-4">
          <div className="space-y-1">
            <FieldLegend className="mb-0">{title}</FieldLegend>
            <FieldDescription>{description}</FieldDescription>
          </div>
          <CollapsibleTrigger asChild>
            <Button
              type="button"
              variant="outline"
              size="sm"
              aria-label={`${shown ? 'Hide' : 'Add'} ${title.toLowerCase()}`}
            >
              {shown ? 'Hide' : 'Add'}
              <ChevronDown className={shown ? 'rotate-180' : undefined} />
            </Button>
          </CollapsibleTrigger>
        </div>
        <CollapsibleContent>
          <FieldGroup>{children}</FieldGroup>
        </CollapsibleContent>
      </FieldSet>
    </Collapsible>
  )
}

/** One parent: every part optional, but details must name them. */
export function ParentSection({
  form,
  error,
  who,
}: {
  form: Form
  error: NcbrsError | null
  who: 'mother' | 'father'
}) {
  const Who = who === 'mother' ? 'Mother' : 'Father'
  const errors = form.formState.errors[who]
  const documentType = useWatch({ control: form.control, name: `${who}.documentType` })

  return (
    <Section
      title={`The ${who}`}
      description="Optional. Record what the family can give."
      hasErrors={errors !== undefined || messagesFor(error, `data.${who}`).length > 0}
    >
      <Field>
        <FieldLabel htmlFor={`${who}.givenNames`}>{Who}’s given names</FieldLabel>
        <Input id={`${who}.givenNames`} {...form.register(`${who}.givenNames`)} autoComplete="off" />
        <FieldError errors={[errors?.givenNames]} />
        <ServerErrors error={error} field={`data.${who}.givenNames`} />
      </Field>

      <Field>
        <FieldLabel htmlFor={`${who}.surname`}>{Who}’s surname</FieldLabel>
        <Input id={`${who}.surname`} {...form.register(`${who}.surname`)} autoComplete="off" />
        <FieldError errors={[errors?.surname]} />
      </Field>

      {who === 'mother' ? (
        <Field>
          <FieldLabel htmlFor="mother.maidenSurname">Mother’s maiden surname</FieldLabel>
          <Input id="mother.maidenSurname" {...form.register('mother.maidenSurname')} autoComplete="off" />
          <FieldError errors={[errors?.maidenSurname]} />
        </Field>
      ) : null}

      <Field>
        <FieldLabel htmlFor={`${who}.dateOfBirth`}>{Who}’s date of birth</FieldLabel>
        <Input id={`${who}.dateOfBirth`} type="date" max={today()} {...form.register(`${who}.dateOfBirth`)} />
        <FieldError errors={[errors?.dateOfBirth]} />
        <ServerErrors error={error} field={`data.${who}.dateOfBirth`} />
      </Field>

      <Field>
        <FieldLabel htmlFor={`${who}.placeOfBirth`}>{Who}’s place of birth</FieldLabel>
        <Input id={`${who}.placeOfBirth`} {...form.register(`${who}.placeOfBirth`)} autoComplete="off" />
        <FieldError errors={[errors?.placeOfBirth]} />
      </Field>

      <Field>
        <FieldLabel htmlFor={`${who}.occupation`}>{Who}’s job</FieldLabel>
        <Input id={`${who}.occupation`} {...form.register(`${who}.occupation`)} autoComplete="off" />
        <FieldDescription>As they say it, for example “teacher” or “cattle keeper”.</FieldDescription>
        <FieldError errors={[errors?.occupation]} />
      </Field>

      <Field>
        <FieldLabel htmlFor={`${who}.address`}>{Who}’s address</FieldLabel>
        <Input
          id={`${who}.address`}
          placeholder="Village, boma, payam, or a landmark"
          {...form.register(`${who}.address`)}
          autoComplete="off"
        />
        <FieldError errors={[errors?.address]} />
      </Field>

      <Field>
        <FieldLabel htmlFor={`${who}.documentType`}>{Who}’s identity document</FieldLabel>
        <Select
          value={documentType ?? ''}
          onValueChange={(value) =>
            form.setValue(`${who}.documentType`, value as NonNullable<ParentDetails['documentType']>, {
              shouldValidate: true,
            })
          }
        >
          <SelectTrigger id={`${who}.documentType`}>
            <SelectValue placeholder="None shown" />
          </SelectTrigger>
          <SelectContent>
            {documentTypes.map((type) => (
              <SelectItem key={type} value={type}>
                {DocumentLabels[type]}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <FieldError errors={[errors?.documentType]} />
      </Field>

      <Field>
        <FieldLabel htmlFor={`${who}.documentNumber`}>{Who}’s document number</FieldLabel>
        <Input id={`${who}.documentNumber`} {...form.register(`${who}.documentNumber`)} autoComplete="off" />
        <FieldDescription>The number only. The document stays with the family.</FieldDescription>
        <FieldError errors={[errors?.documentNumber]} />
        <ServerErrors error={error} field={`data.${who}.documentNumber`} />
      </Field>
    </Section>
  )
}

/** The parents' marriage: statutory, customary or religious. */
export function MarriageSection({ form, error }: { form: Form; error: NcbrsError | null }) {
  const errors = form.formState.errors.marriage

  return (
    <Section
      title="The parents’ marriage"
      description="Optional. Statutory, customary or religious."
      hasErrors={errors !== undefined || messagesFor(error, 'data.marriage').length > 0}
    >
      <Field>
        <FieldLabel htmlFor="marriage.date">Date of the parents’ marriage</FieldLabel>
        <Input id="marriage.date" type="date" max={today()} {...form.register('marriage.date')} />
        <FieldError errors={[errors?.date]} />
        <ServerErrors error={error} field="data.marriage.date" />
      </Field>

      <Field>
        <FieldLabel htmlFor="marriage.certificateNumber">Marriage certificate number</FieldLabel>
        <Input id="marriage.certificateNumber" {...form.register('marriage.certificateNumber')} autoComplete="off" />
        <FieldError errors={[errors?.certificateNumber]} />
      </Field>
    </Section>
  )
}

/** A document shown as proof of address, for example a utility bill. */
export function ProofOfAddressSection({ form }: { form: Form }) {
  const errors = form.formState.errors.proofOfAddress

  return (
    <Section
      title="Proof of address"
      description="Optional. For example a utility bill."
      hasErrors={errors !== undefined}
    >
      <Field>
        <FieldLabel htmlFor="proofOfAddress.kind">What was shown</FieldLabel>
        <Input
          id="proofOfAddress.kind"
          placeholder="For example: utility bill"
          {...form.register('proofOfAddress.kind')}
          autoComplete="off"
        />
        <FieldError errors={[errors?.kind]} />
      </Field>

      <Field>
        <FieldLabel htmlFor="proofOfAddress.reference">Its reference</FieldLabel>
        <Input id="proofOfAddress.reference" {...form.register('proofOfAddress.reference')} autoComplete="off" />
        <FieldError errors={[errors?.reference]} />
      </Field>
    </Section>
  )
}

/** A parent's details as the registry takes them, or nothing when none were given. */
export function parentForRequest(details: RegistrationInput['mother']): ParentDetails | undefined {
  const text = (value?: string) => (value && value.trim() !== '' ? value.trim() : undefined)
  const out: ParentDetails = {
    givenNames: text(details?.givenNames),
    surname: text(details?.surname),
    maidenSurname: text(details?.maidenSurname),
    dateOfBirth: text(details?.dateOfBirth),
    placeOfBirth: text(details?.placeOfBirth),
    occupation: text(details?.occupation),
    address: text(details?.address),
    documentType: details?.documentType ? details.documentType : undefined,
    documentNumber: text(details?.documentNumber),
  }

  return Object.values(out).some((value) => value !== undefined) ? out : undefined
}

/** What the server said about this field: kept beside the form's own message, because the server's decided. */
export function ServerErrors({ error, field }: { error: NcbrsError | null; field: string }) {
  const messages = messagesFor(error, field)

  if (messages.length === 0) {
    return null
  }

  return <FieldError>{messages.join(' ')}</FieldError>
}
