import { z } from 'zod'

/**
 * The shape of a registration as the form collects it.
 *
 * **This mirrors the server's validator; it does not replace it.** Every rule
 * here exists again in `RegisterBirthRequestValidator`, and the server's copy
 * is the one that decides. What this buys is the registrar finding out about a
 * typo before submitting a twenty-field form, not a second opinion about what
 * the register will accept — which is why nothing here is stricter than the
 * server, only faster.
 *
 * The one rule deliberately absent is lateness. Whether a birth is outside the
 * statutory window depends on a number set in law and held as server
 * configuration, so the form asks (`GET /api/BirthRecords/registration-rules`)
 * rather than deciding. A copy of that number here would keep answering
 * confidently after the Act changed.
 */
export const registrationSchema = z.object({
  facilityId: z.uuid('Choose the facility where the birth happened.'),

  childFullName: z
    .string()
    .trim()
    .min(1, 'The child’s name is required.')
    .max(200, 'The name must be 200 characters or fewer.'),

  dateOfBirth: z
    .string()
    .min(1, 'The date of birth is required.')
    .refine(notInTheFuture, 'The date of birth cannot be in the future.'),

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

  motherFullName: z.string().trim().max(200, 'The name must be 200 characters or fewer.').optional(),
  fatherFullName: z.string().trim().max(200, 'The name must be 200 characters or fewer.').optional(),

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
 * "tomorrow" in Lusaka must not be "today" to a server in another one.
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
