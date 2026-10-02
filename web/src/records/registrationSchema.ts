import { z } from 'zod'

/**
 * The shape of a registration as the form collects it.
 *
 * **This mirrors the server's validator; it does not replace it.** Every rule
 * here exists again in `RegisterBirthRequestValidator`, and the server's copy
 * is the one that decides. What this buys is the registrar finding out about a
 * typo before submitting a long form, not a second opinion about what the
 * register will accept — which is why nothing here is stricter than the
 * server, only faster.
 *
 * The one rule deliberately absent is lateness. Whether a birth is outside the
 * statutory window depends on a number set in law and held as server
 * configuration, so the form asks (`GET /api/BirthRecords/registration-rules`)
 * rather than deciding. A copy of that number here would keep answering
 * confidently after the Act changed.
 *
 * The fuller registration (2026-10-02): the child is named in parts, the place
 * of birth is required, and the parents' details, marriage and proof of
 * address are all optional. A parent's details must name them, and an identity
 * document is recorded as its type and number together.
 */

export const placesOfBirth = ['ThisFacility', 'OtherHealthFacility', 'Home', 'Elsewhere'] as const
export const documentTypes = ['Passport', 'BirthCertificate', 'DrivingLicence', 'NationalId'] as const

function optionalText(max: number) {
  return z.string().trim().max(max, `This must be ${max} characters or fewer.`).optional()
}

const parent = z.object({
  givenNames: optionalText(100),
  surname: optionalText(100),
  maidenSurname: optionalText(100),
  dateOfBirth: z.string().optional(),
  placeOfBirth: optionalText(200),
  occupation: optionalText(200),
  address: optionalText(300),
  documentType: z.union([z.literal(''), z.enum(documentTypes)]).optional(),
  documentNumber: optionalText(50),
})

export type ParentInput = z.input<typeof parent>

/** Whether any part of a parent's details was filled in. */
export function hasAny(details: ParentInput | undefined): boolean {
  return Object.values(details ?? {}).some(
    (value) => value !== undefined && String(value).trim() !== '',
  )
}

export const registrationSchema = z
  .object({
    facilityId: z.uuid('Choose the facility where the birth is being registered.'),

    childGivenNames: z
      .string()
      .trim()
      .min(1, 'The child’s given names are required.')
      .max(100, 'This must be 100 characters or fewer.'),

    childSurname: z
      .string()
      .trim()
      .min(1, 'The child’s surname is required.')
      .max(100, 'This must be 100 characters or fewer.'),

    dateOfBirth: z
      .string()
      .min(1, 'The date of birth is required.')
      .refine(notInTheFuture, 'The date of birth cannot be in the future.'),

    placeOfBirthKind: z.enum(placesOfBirth, { error: 'Say where the birth happened.' }),

    placeOfBirth: optionalText(200),

    sex: z.enum(['Male', 'Female', 'Undetermined'], {
      error: 'Choose Male, Female or Undetermined.',
    }),

    plurality: z.enum(['Singleton', 'Twin', 'Triplet', 'HigherOrderMultiple'], {
      error: 'Choose how many were born.',
    }),

    // Optional clinical measurements. Blank is a real answer -- a village post
    // may have no scale -- and must not become 0, which would aggregate into
    // the national low-birth-weight figure as a 0g baby.
    birthWeightGrams: optionalNumber(200, 9999, 'The birth weight must be between 200g and 9999g.'),
    gestationalAgeWeeks: optionalNumber(16, 45, 'The gestational age must be between 16 and 45 weeks.'),
    birthOrder: optionalNumber(1, 10, 'The birth order must be between 1 and 10.'),

    mother: parent.optional(),
    father: parent.optional(),

    marriage: z
      .object({
        date: z
          .string()
          .optional()
          .refine((value) => !value || notInTheFuture(value), 'The marriage date cannot be in the future.'),
        certificateNumber: optionalText(50),
      })
      .optional(),

    proofOfAddress: z
      .object({
        kind: optionalText(100),
        reference: optionalText(100),
      })
      .optional(),

    // Only collected, and only sent, when the birth falls outside the window.
    // Supplying it for an on-time birth is refused by the API rather than
    // ignored: it means the device and the registry disagree about the date.
    lateRegistration: z
      .object({
        evidenceType: z.enum(
          [
            'HealthFacilityRecord',
            'AntenatalOrDeliveryCard',
            'ImmunisationRecord',
            'BirthAttendantAttestation',
            'ReligiousRecord',
            'SchoolRecord',
            'SwornAffidavit',
            'CourtOrder',
          ],
          { error: 'Choose what the late registration is supported by.' },
        ),
        evidenceReference: z.string().trim().max(200).optional(),
        declarantName: z
          .string()
          .trim()
          .min(1, 'Someone must stand behind the claim.')
          .max(200, 'The name must be 200 characters or fewer.'),
        declarantRelationship: z
          .string()
          .trim()
          .min(1, 'State their standing — mother, father, guardian.')
          .max(100, 'This must be 100 characters or fewer.'),
      })
      .optional(),
  })
  .superRefine((value, context) => {
    if (value.placeOfBirthKind !== 'ThisFacility' && !value.placeOfBirth?.trim()) {
      context.addIssue({
        code: 'custom',
        path: ['placeOfBirth'],
        message: 'Say where: the facility, village or place.',
      })
    }

    for (const who of ['mother', 'father'] as const) {
      const details = value[who]
      if (!hasAny(details)) {
        continue
      }

      if (!details?.givenNames?.trim() && !details?.surname?.trim()) {
        context.addIssue({
          code: 'custom',
          path: [who, 'givenNames'],
          message: `Give the ${who}’s given names or surname, or leave the ${who}’s details empty.`,
        })
      }

      if (details?.documentType && !details.documentNumber?.trim()) {
        context.addIssue({
          code: 'custom',
          path: [who, 'documentNumber'],
          message: 'Give the document’s number.',
        })
      }

      if (!details?.documentType && details?.documentNumber?.trim()) {
        context.addIssue({
          code: 'custom',
          path: [who, 'documentType'],
          message: 'Say which document this is.',
        })
      }

      // A parent born after their child is a slip in one date or the other.
      if (details?.dateOfBirth && value.dateOfBirth && details.dateOfBirth >= value.dateOfBirth) {
        context.addIssue({
          code: 'custom',
          path: [who, 'dateOfBirth'],
          message: `The ${who}’s date of birth must be before the child’s.`,
        })
      }
    }
  })

export type RegistrationInput = z.input<typeof registrationSchema>
export type Registration = z.output<typeof registrationSchema>

/**
 * A number field that may be left blank.
 *
 * Blank has to survive as "not measured" all the way to the wire. A village
 * post with no scale records no weight, and turning that into 0 would put a
 * 0g baby into the national low-birth-weight figure — a number nobody could
 * tell was wrong by looking at it.
 */
function optionalNumber(min: number, max: number, message: string) {
  return z
    .union([z.literal(''), z.coerce.number()])
    .optional()
    .refine(
      (value) => value === '' || value === undefined || (value >= min && value <= max),
      message,
    )
}

/**
 * A date of birth is a calendar date. Compared as text against today's date in
 * the same form, so the answer does not change with the reader's timezone —
 * "tomorrow" in Juba must not be "today" to a server in another one.
 */
function notInTheFuture(value: string): boolean {
  return value <= today()
}

export function today(): string {
  return new Date().toISOString().slice(0, 10)
}

/**
 * Whole days between a birth and now, on the calendar rather than the clock.
 *
 * Used only to decide whether to *ask* for late-registration evidence. The
 * server makes the binding decision against the device's capture time, and
 * this is the form trying to ask the right questions before it gets there.
 */
export function daysSinceBirth(dateOfBirth: string): number {
  if (!dateOfBirth) {
    return 0
  }

  const born = Date.parse(`${dateOfBirth}T00:00:00Z`)
  const now = Date.parse(`${today()}T00:00:00Z`)

  if (Number.isNaN(born)) {
    return 0
  }

  return Math.floor((now - born) / (24 * 60 * 60 * 1000))
}
