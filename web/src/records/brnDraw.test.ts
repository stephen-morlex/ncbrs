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

describe('when the registry refuses the number itself', () => {
  it('draws a different one next time', async () => {
    // A BRN already registered can never be registered again. Holding it
    // across retries leaves the form refusing forever, however many times the
    // registrar presses it.
    const draw = vi.fn().mockResolvedValueOnce('200000').mockResolvedValueOnce('200002')
    const brn = createBrnDraw(draw)

    expect(await brn.forAttempt(FacilityId)).toBe('200000')

    brn.discard()

    expect(await brn.forAttempt(FacilityId)).toBe('200002')
    expect(draw).toHaveBeenCalledTimes(2)
  })

  it('holds the number for every other kind of refusal', async () => {
    // The distinction this rests on. A missing declarant says nothing about
    // the number, and drawing a fresh one per fix would leave a trail of gaps
    // nobody could account for.
    const draw = vi.fn().mockResolvedValue('200000')
    const brn = createBrnDraw(draw)

    await brn.forAttempt(FacilityId)
    await brn.forAttempt(FacilityId)
    await brn.forAttempt(FacilityId)

    expect(draw).toHaveBeenCalledTimes(1)
    expect(brn.held()).toBe('200000')
  })
})
