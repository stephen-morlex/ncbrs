/**
 * Which corrections take effect now, and which wait for a second registrar.
 *
 * The split is the server's (`AmendmentService`), mirrored here so the form can
 * say which track a field is on **before** the registrar submits — not after.
 * That ordering is the whole point: someone correcting a child's name needs to
 * know it is going to review while the family is still in front of them, so
 * they do not say it is done.
 *
 * The immediate track is exactly the clinical measurements, which describe the
 * *event*. Everything describing **who the record is about** waits: the child's
 * name, date of birth and sex, and both parents' names.
 *
 * Parents' names are on the approval track without being on the certificate,
 * and that is deliberate upstream. Nothing printed changes when a father's name
 * is corrected — but filiation does, and that is the field a disputed paternity
 * would be rewritten through and the one an inheritance claim later turns on.
 */
export const ImmediateFields = [
  'birthWeightGrams',
  'gestationalAgeWeeks',
  'birthOrder',
] as const

/** A parent's details, as the form names them: `mother.address`. */
const ParentParts = [
  'givenNames',
  'surname',
  'maidenSurname',
  'dateOfBirth',
  'placeOfBirth',
  'occupation',
  'address',
  'documentType',
  'documentNumber',
] as const

export const ApprovalFields = [
  'childFullName',
  'dateOfBirth',
  'sex',
  'motherFullName',
  'fatherFullName',
  // The fuller registration's fields all wait too: only the clinical
  // measurements above take effect at once.
  'childGivenNames',
  'childSurname',
  'placeOfBirthKind',
  'placeOfBirth',
  ...ParentParts.map((part) => `mother.${part}` as const),
  ...ParentParts.filter((part) => part !== 'maidenSurname').map((part) => `father.${part}` as const),
  'marriage.date',
  'marriage.certificateNumber',
  'proofOfAddress.kind',
  'proofOfAddress.reference',
] as const

/**
 * Fields whose correction withdraws a certificate that has already been issued.
 *
 * A **subset** of the approval track, not the same list. A certificate's
 * signature covers the child's name, date of birth and sex; correcting a
 * parent's name invalidates no printed document. Collapsing the two would
 * either warn about withdrawal that will not happen, or fail to warn about one
 * that will.
 */
export const CertificateFields = [
  'childFullName',
  // The parts recompose the full name the certificate signs.
  'childGivenNames',
  'childSurname',
  'dateOfBirth',
  'sex',
] as const

/**
 * A field as the registry names it in a correction (`ChildFullName`,
 * `Mother.Address`), as a registrar reads it. A history or a queue that says
 * "Mother.DocumentNumber" asks the reader to translate first.
 */
export function fieldLabel(field: string): string {
  const whole: Record<string, string> = {
    ChildFullName: 'Child’s full name',
    ChildGivenNames: 'Child’s given names',
    ChildSurname: 'Child’s surname',
    DateOfBirth: 'Date of birth',
    Sex: 'Sex',
    BirthWeightGrams: 'Birth weight',
    GestationalAgeWeeks: 'Gestational age',
    BirthOrder: 'Birth order',
    MotherFullName: 'Mother’s full name',
    FatherFullName: 'Father’s full name',
    PlaceOfBirthKind: 'Place of birth',
    PlaceOfBirth: 'Where the birth happened',
    'Marriage.Date': 'Date of the parents’ marriage',
    'Marriage.CertificateNumber': 'Marriage certificate number',
    'ProofOfAddress.Kind': 'Proof of address',
    'ProofOfAddress.Reference': 'Proof of address reference',
  }
  const who: Record<string, string> = { Mother: 'Mother’s', Father: 'Father’s' }
  const parts: Record<string, string> = {
    GivenNames: 'given names',
    Surname: 'surname',
    MaidenSurname: 'maiden surname',
    DateOfBirth: 'date of birth',
    PlaceOfBirth: 'place of birth',
    Occupation: 'job',
    Address: 'address',
    DocumentType: 'identity document',
    DocumentNumber: 'document number',
  }

  if (whole[field]) {
    return whole[field]
  }

  const [head, part] = field.split('.')
  return part && who[head] && parts[part] ? `${who[head]} ${parts[part]}` : field
}

export type CorrectableField =
  | (typeof ImmediateFields)[number]
  | (typeof ApprovalFields)[number]

export function needsApproval(field: string): boolean {
  return (ApprovalFields as readonly string[]).includes(field)
}

export function withdrawsCertificate(field: string): boolean {
  return (CertificateFields as readonly string[]).includes(field)
}

/**
 * The fields a correction is actually changing.
 *
 * **A correction names only what was wrong.** The form is prefilled with what
 * the record says, so sending everything back would submit a dozen unchanged
 * fields as corrections — each one an audit row asserting a change that did not
 * happen, and each one dragging its field onto the approval track for no
 * reason. A name resubmitted identically would send a record to review.
 *
 * Blank is compared as blank rather than coerced, so clearing a mother's name
 * that was never recorded is correctly seen as no change at all.
 */
export function changedFields<T extends Record<string, unknown>>(
  original: T,
  edited: T,
): Partial<T> {
  const changes: Partial<T> = {}

  for (const key of Object.keys(edited) as (keyof T)[]) {
    if (normalise(edited[key]) !== normalise(original[key])) {
      changes[key] = edited[key]
    }
  }

  return changes
}

/**
 * Empty string, null and undefined all mean "nothing here" on a form, and must
 * compare equal — otherwise a field the record has never held would look
 * changed the moment the form rendered it as an empty input.
 */
function normalise(value: unknown): string {
  if (value === null || value === undefined) {
    return ''
  }

  return String(value).trim()
}
