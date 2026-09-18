import { describe, expect, it } from 'vitest'
import { daysSinceBirth, registrationSchema, today } from './registrationSchema'

function valid(overrides: Record<string, unknown> = {}) {
  return {
    facilityId: '0199a1b2-0001-7000-8000-000000000001',
    childFullName: 'Ayen Deng',
    dateOfBirth: '2026-06-01',
    sex: 'Female',
    plurality: 'Singleton',
    ...overrides,
  }
}

describe('registrationSchema', () => {
  it('accepts a minimal registration', () => {
    expect(registrationSchema.safeParse(valid()).success).toBe(true)
  })

  it('refuses a date of birth in the future', () => {
    const tomorrow = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString().slice(0, 10)

    const result = registrationSchema.safeParse(valid({ dateOfBirth: tomorrow }))

    expect(result.success).toBe(false)
  })

  it('keeps a blank measurement blank rather than turning it into zero', () => {
    // The rule this file exists to protect. A village post with no scale
    // records no weight; coerced to 0 it would enter the national
    // low-birth-weight figure as a 0g baby, and nobody reading the figure
    // could tell.
    const result = registrationSchema.safeParse(valid({ birthWeightGrams: '' }))

    expect(result.success).toBe(true)
    expect(result.data?.birthWeightGrams).not.toBe(0)
  })

  it('accepts a measurement inside the clinical range', () => {
    const result = registrationSchema.safeParse(valid({ birthWeightGrams: '3200' }))

    expect(result.success).toBe(true)
    expect(result.data?.birthWeightGrams).toBe(3200)
  })

  it('refuses an implausible birth weight', () => {
    expect(registrationSchema.safeParse(valid({ birthWeightGrams: '50' })).success).toBe(false)
    expect(registrationSchema.safeParse(valid({ birthWeightGrams: '15000' })).success).toBe(false)
  })

  it('requires a declarant once late-registration evidence is given', () => {
    // Someone must stand behind the claim. Evidence with nobody attached to it
    // is exactly what the late process exists to prevent.
    const result = registrationSchema.safeParse(
      valid({
        lateRegistration: {
          evidenceType: 'BirthAttendantAttestation',
          declarantName: '',
          declarantRelationship: 'mother',
        },
      }),
    )

    expect(result.success).toBe(false)
  })

  it('accepts complete late-registration evidence', () => {
    const result = registrationSchema.safeParse(
      valid({
        lateRegistration: {
          evidenceType: 'BirthAttendantAttestation',
          evidenceReference: 'TBA attestation 44/2026',
          declarantName: 'Nyandeng Deng',
          declarantRelationship: 'mother',
        },
      }),
    )

    expect(result.success).toBe(true)
  })
})

describe('daysSinceBirth', () => {
  it('is zero for a birth today', () => {
    expect(daysSinceBirth(today())).toBe(0)
  })

  it('counts whole calendar days', () => {
    const born = new Date(Date.now() - 100 * 24 * 60 * 60 * 1000).toISOString().slice(0, 10)

    expect(daysSinceBirth(born)).toBe(100)
  })

  it('is zero rather than NaN for an empty or unparseable date', () => {
    // It decides whether to *ask* for evidence. NaN would compare false
    // against any window and quietly stop asking, which is the one direction
    // this must not fail in.
    expect(daysSinceBirth('')).toBe(0)
    expect(daysSinceBirth('not-a-date')).toBe(0)
  })
})
