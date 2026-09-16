import { describe, expect, it, vi } from 'vitest'
import { createBrnDraw } from './brnDraw'

const FacilityId = '0199a1b2-0001-7000-8000-000000000001'

describe('createBrnDraw', () => {
  it('draws a number for the first attempt', async () => {
    const draw = vi.fn().mockResolvedValue('100001')
    const brn = createBrnDraw(draw)

    expect(await brn.forAttempt(FacilityId)).toBe('100001')
    expect(draw).toHaveBeenCalledTimes(1)
  })

  it('does not burn a second number when a submission is refused and retried', async () => {
    // The rule this exists for. A BRN is scarce and permanent, and drawing one
    // advances the facility's counter for good; a registrar fixing three
    // validation errors must not leave three unexplained gaps in the register.
    const draw = vi.fn().mockResolvedValueOnce('100001').mockResolvedValueOnce('100002')
    const brn = createBrnDraw(draw)

    const first = await brn.forAttempt(FacilityId)
    const second = await brn.forAttempt(FacilityId)
    const third = await brn.forAttempt(FacilityId)

    expect(first).toBe('100001')
    expect(second).toBe('100001')
    expect(third).toBe('100001')
    expect(draw).toHaveBeenCalledTimes(1)
  })

  it('draws afresh once a number has been spent', async () => {
    // Registering the next birth is a new registration, not another attempt at
    // the last one, and must never reuse a number already on a record.
    const draw = vi.fn().mockResolvedValueOnce('100001').mockResolvedValueOnce('100002')
    const brn = createBrnDraw(draw)

    await brn.forAttempt(FacilityId)
    brn.spend()

    expect(await brn.forAttempt(FacilityId)).toBe('100002')
    expect(draw).toHaveBeenCalledTimes(2)
  })

  it('remembers nothing when the draw itself failed', async () => {
    // The facility may have exhausted its range, or the caller may not be
    // permitted for it. Either way the next attempt should genuinely ask
    // again rather than replay a refusal.
    const draw = vi.fn().mockResolvedValueOnce(null).mockResolvedValueOnce('100001')
    const brn = createBrnDraw(draw)

    expect(await brn.forAttempt(FacilityId)).toBeNull()
    expect(brn.held()).toBeNull()

    expect(await brn.forAttempt(FacilityId)).toBe('100001')
    expect(draw).toHaveBeenCalledTimes(2)
  })
})
