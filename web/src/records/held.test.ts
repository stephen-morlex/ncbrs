import { describe, expect, it } from 'vitest'
import { heldDays } from './held'

describe('heldDays', () => {
  it('counts the days a post was without a link', () => {
    expect(heldDays('2026-06-12T08:30:00Z', '2026-09-10T11:02:00Z')).toBe(90)
  })

  it('is zero for an online registration, where both timestamps are one instant', () => {
    expect(heldDays('2026-09-10T11:02:03Z', '2026-09-10T11:02:03Z')).toBe(0)
  })

  it('is zero when the record predates the registry keeping the capture time', () => {
    expect(heldDays(null, '2026-09-10T11:02:00Z')).toBe(0)
    expect(heldDays(undefined, '2026-09-10T11:02:00Z')).toBe(0)
  })

  it('counts calendar days, not elapsed hours', () => {
    // Twenty-three hours apart, but two different days. A registrar reading a
    // record cares which day it arrived, not how many hours of clock ran.
    expect(heldDays('2026-09-10T23:30:00Z', '2026-09-11T22:30:00Z')).toBe(1)

    // Ninety minutes apart and the same day.
    expect(heldDays('2026-09-10T08:00:00Z', '2026-09-10T09:30:00Z')).toBe(0)
  })

  it('never reports a negative wait', () => {
    // A device clock inside the skew tolerance may be ahead of the server, so
    // a registration can arrive on the calendar day before it was captured.
    // The registry accepts that; the screen must not say "held -1 days".
    expect(heldDays('2026-09-11T02:00:00Z', '2026-09-10T23:00:00Z')).toBe(0)
  })
})
