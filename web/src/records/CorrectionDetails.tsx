import { Field, FieldDescription, FieldGroup, FieldLabel, FieldLegend, FieldSet } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { DocumentLabels, PlaceLabels } from './RegistrationSections'
import { documentTypes, placesOfBirth } from './registrationSchema'

/** The form's values, by the names the request uses: `mother.address`. */
export type CorrectionDraft = Record<string, string>

type Edit = (field: string, value: string) => void

/**
 * The fuller registration's fields, as a correction edits them: the place of
 * birth, each parent, the marriage and proof of address. All wait for a second
 * registrar.
 *
 * **An emptied box clears what it said** (an address, a job), which the
 * registry stores as nothing recorded. Dates and the document type can be
 * corrected but not cleared, so they are not offered empty.
 */
export function CorrectionDetails({
  draft,
  edit,
  parentNamesInParts,
  restricted,
}: {
  draft: CorrectionDraft
  edit: Edit
  /** Whether each parent's name was recorded in parts, or whole. */
  parentNamesInParts: { mother: boolean; father: boolean }
  /** Addresses and document numbers were withheld from this caller. */
  restricted: boolean
}) {
  const kind = draft['placeOfBirthKind'] ?? ''

  return (
    <>
      <div className="grid items-start gap-5 md:grid-cols-2">
        <Field>
          <FieldLabel htmlFor="placeOfBirthKind">Place of birth</FieldLabel>
          <Select value={kind} onValueChange={(value) => edit('placeOfBirthKind', value)}>
            <SelectTrigger id="placeOfBirthKind">
              <SelectValue placeholder="Not recorded" />
            </SelectTrigger>
            <SelectContent>
              {placesOfBirth.map((place) => (
                <SelectItem key={place} value={place}>
                  {PlaceLabels[place]}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>

        {kind && kind !== 'ThisFacility' ? (
          <Field>
            <FieldLabel htmlFor="placeOfBirth">Where</FieldLabel>
            <Input id="placeOfBirth" value={draft['placeOfBirth'] ?? ''} onChange={(event) => edit('placeOfBirth', event.target.value)} />
          </Field>
        ) : null}
      </div>

      <Parent who="mother" draft={draft} edit={edit} inParts={parentNamesInParts.mother} restricted={restricted} />
      <Parent who="father" draft={draft} edit={edit} inParts={parentNamesInParts.father} restricted={restricted} />

      <FieldSet>
        <FieldLegend>The parents’ marriage</FieldLegend>
        <FieldGroup className="grid items-start gap-5 md:grid-cols-2">
          <Field>
            <FieldLabel htmlFor="marriage.date">Date of the parents’ marriage</FieldLabel>
            <Input id="marriage.date" type="date" value={draft['marriage.date'] ?? ''} onChange={(event) => edit('marriage.date', event.target.value)} />
          </Field>
          <Field>
            <FieldLabel htmlFor="marriage.certificateNumber">Marriage certificate number</FieldLabel>
            <Input
              id="marriage.certificateNumber"
              value={draft['marriage.certificateNumber'] ?? ''}
              disabled={restricted}
              onChange={(event) => edit('marriage.certificateNumber', event.target.value)}
            />
          </Field>
        </FieldGroup>
      </FieldSet>

      <FieldSet>
        <FieldLegend>Proof of address</FieldLegend>
        <FieldGroup className="grid items-start gap-5 md:grid-cols-2">
          <Field>
            <FieldLabel htmlFor="proofOfAddress.kind">What was shown</FieldLabel>
            <Input id="proofOfAddress.kind" value={draft['proofOfAddress.kind'] ?? ''} onChange={(event) => edit('proofOfAddress.kind', event.target.value)} />
          </Field>
          <Field>
            <FieldLabel htmlFor="proofOfAddress.reference">Its reference</FieldLabel>
            <Input
              id="proofOfAddress.reference"
              value={draft['proofOfAddress.reference'] ?? ''}
              disabled={restricted}
              onChange={(event) => edit('proofOfAddress.reference', event.target.value)}
            />
          </Field>
        </FieldGroup>
      </FieldSet>

      <FieldDescription>
        Empty a box to remove what it says. A date or a document type can be corrected but not
        removed.
      </FieldDescription>
    </>
  )
}

function Parent({
  who,
  draft,
  edit,
  inParts,
  restricted,
}: {
  who: 'mother' | 'father'
  draft: CorrectionDraft
  edit: Edit
  inParts: boolean
  restricted: boolean
}) {
  const Who = who === 'mother' ? 'Mother' : 'Father'
  const text = (part: string, label: string, options: { disabled?: boolean; type?: string } = {}) => (
    <Field>
      <FieldLabel htmlFor={`${who}.${part}`}>{label}</FieldLabel>
      <Input
        id={`${who}.${part}`}
        type={options.type}
        value={draft[`${who}.${part}`] ?? ''}
        disabled={options.disabled}
        onChange={(event) => edit(`${who}.${part}`, event.target.value)}
      />
    </Field>
  )

  return (
    <FieldSet>
      <FieldLegend>The {who}</FieldLegend>
      <FieldDescription>
        No certificate is withdrawn by correcting these, but filiation changes, which is why they
        wait for a reviewer.
      </FieldDescription>
      <FieldGroup className="grid items-start gap-5 md:grid-cols-2">
        {/* Corrected the way it was recorded: in parts, or whole. A name
            recorded whole is corrected whole; splitting it is a decision for
            the person who knows the family, not a guess here. */}
        {inParts ? (
          <>
            {text('givenNames', `${Who}’s given names`)}
            {text('surname', `${Who}’s surname`)}
          </>
        ) : (
          <Field className="md:col-span-2">
            <FieldLabel htmlFor={`${who}FullName`}>{Who}’s full name</FieldLabel>
            <Input
              id={`${who}FullName`}
              value={draft[`${who}FullName`] ?? ''}
              onChange={(event) => edit(`${who}FullName`, event.target.value)}
            />
          </Field>
        )}
        {who === 'mother' ? text('maidenSurname', 'Mother’s maiden surname') : null}
        {text('dateOfBirth', `${Who}’s date of birth`, { type: 'date' })}
        {text('placeOfBirth', `${Who}’s place of birth`)}
        {text('occupation', `${Who}’s job`)}
        {text('address', `${Who}’s address`, { disabled: restricted })}
        <Field>
          <FieldLabel htmlFor={`${who}.documentType`}>{Who}’s identity document</FieldLabel>
          <Select value={draft[`${who}.documentType`] ?? ''} onValueChange={(value) => edit(`${who}.documentType`, value)}>
            <SelectTrigger id={`${who}.documentType`}>
              <SelectValue placeholder="None recorded" />
            </SelectTrigger>
            <SelectContent>
              {documentTypes.map((type) => (
                <SelectItem key={type} value={type}>
                  {DocumentLabels[type]}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
        {text('documentNumber', `${Who}’s document number`, { disabled: restricted })}
      </FieldGroup>
    </FieldSet>
  )
}
