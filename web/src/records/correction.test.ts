import { describe, expect, it } from 'vitest'
import { changedFields, needsApproval, withdrawsCertificate } from './correction'

describe('the two tracks', () => {
  it('puts the clinical measurements on the immediate track', () => {
    // These describe the event, not who the record is about.
    expect(needsApproval('birthWeightGrams')).toBe(false)
    expect(needsApproval('gestationalAgeWeeks')).toBe(false)
    expect(needsApproval('birthOrder')).toBe(false)
  })

  it('sends anything describing who the record is about to review', () => {
    expect(needsApproval('childFullName')).toBe(true)
    expect(needsApproval('dateOfBirth')).toBe(true)
    expect(needsApproval('sex')).toBe(true)
  })

  it('sends a parent’s name to review even though no certificate is affected', () => {
    // The two lists answer different questions. Nothing printed changes when a
    // father's name is corrected, but filiation does -- it is what a disputed
    // paternity is rewritten through and what an inheritance claim turns on.
    expect(needsApproval('motherFullName')).toBe(true)
    expect(needsApproval('fatherFullName')).toBe(true)

    expect(withdrawsCertificate('motherFullName')).toBe(false)
    expect(withdrawsCertificate('fatherFullName')).toBe(false)
  })

  it('withdraws a certificate only for the fields its signature covers', () => {
    expect(withdrawsCertificate('childFullName')).toBe(true)
    expect(withdrawsCertificate('dateOfBirth')).toBe(true)
    expect(withdrawsCertificate('sex')).toBe(true)

    expect(withdrawsCertificate('birthWeightGrams')).toBe(false)
  })
})

describe('changedFields', () => {
  it('names only what was actually changed', () => {
    const original = { childFullName: 'Ayen Deng', birthWeightGrams: 3200 }
    const edited = { childFullName: 'Ayen Deng', birthWeightGrams: 3350 }

    expect(changedFields(original, edited)).toEqual({ birthWeightGrams: 3350 })
  })

  it('sends nothing when nothing was touched', () => {
    // The form is prefilled, so submitting it untouched must not file a
    // correction -- every unchanged field would be an audit row asserting a
    // change that did not happen.
    const record = { childFullName: 'Ayen Deng', birthWeightGrams: 3200 }

    expect(changedFields(record, { ...record })).toEqual({})
  })

  it('does not treat a resubmitted identical name as a correction', () => {
    // The sharpest case. A name resubmitted unchanged would otherwise send the
    // whole record to a reviewer for nothing.
    const original = { childFullName: 'Ayen Deng' }

    expect(changedFields(original, { childFullName: '  Ayen Deng  ' })).toEqual({})
  })

  it('treats blank, null and undefined as the same absence', () => {
    // A mother's name the record never held renders as an empty input. It must
    // not look changed the moment the form appears.
    const original = { motherFullName: null as string | null }

    expect(changedFields(original, { motherFullName: '' })).toEqual({})
  })

  it('sees a value being supplied where there was none', () => {
    const original = { motherFullName: '' }

    expect(changedFields(original, { motherFullName: 'Nyandeng Deng' })).toEqual({
      motherFullName: 'Nyandeng Deng',
    })
  })

  it('sees a value being cleared', () => {
    const original = { fatherFullName: 'John Deng' }

    expect(changedFields(original, { fatherFullName: '' })).toEqual({ fatherFullName: '' })
  })
})
