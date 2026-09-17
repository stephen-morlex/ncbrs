/**
 * Draws a registration number for a web-filed birth, once.
 *
 * **A browser is not a facility device.** A device holds a block of BRNs
 * granted in advance precisely so it can register with no connectivity; a
 * browser has neither block nor enrolment. So rather than inventing a number,
 * this draws one — a block of exactly one — from the facility's own
 * pre-approved range, through the same endpoint and the same
 * `[ConcurrencyCheck]` counter that grants device blocks. Design decision #2
 * stays intact: the number comes from the facility's range, the counter
 * advances atomically, and it can never collide with a block granted to a
 * device next month.
 *
 * **Drawn once and held.** A BRN is a scarce, permanent identifier and drawing
 * one advances the facility's counter for good. If a refused submission drew a
 * fresh number each time, a registrar fixing three validation errors would burn
 * three BRNs and leave three gaps in the register that nobody could account
 * for. So the number is kept across retries and released only when it is
 * actually spent on a registration.
 *
 * A number is still lost if the registrar abandons the form after a refusal.
 * That is a gap in a sequence rather than a collision, and it is the cost of
 * not asking the centre to reserve numbers it cannot tell are still wanted.
 */
export interface BrnDraw {
  /** The number for this attempt, drawing one only if none is held. */
  forAttempt(facilityId: string): Promise<string | null>

  /** Called once the number is registered, so the next birth draws afresh. */
  spend(): void

  /**
   * Called when the registry refused the **number itself**, so the next
   * attempt draws a different one.
   *
   * Holding a number across retries is right when the number was not what was
   * wrong — a missing declarant, an implausible weight. It is exactly wrong
   * when the registry says that BRN is already registered: retrying with the
   * same number cannot ever succeed, and the form would sit there refusing
   * forever however many times the registrar pressed it.
   *
   * Discarding costs a number from the facility's range. That is the cheaper
   * mistake: a gap in a sequence versus a registration that can never be made.
   */
  discard(): void

  /** What is currently held, for tests and for diagnostics. */
  held(): string | null
}

export interface BrnBlockDrawer {
  (facilityId: string): Promise<string | null>
}

export function createBrnDraw(draw: BrnBlockDrawer): BrnDraw {
  let current: string | null = null

  return {
    async forAttempt(facilityId: string) {
      if (current !== null) {
        return current
      }

      const drawn = await draw(facilityId)

      // A failed draw is not remembered. The facility may have been out of
      // numbers, or the caller unauthorised for it; either way the next
      // attempt should genuinely ask again rather than replay a null.
      if (drawn !== null) {
        current = drawn
      }

      return drawn
    },

    spend() {
      current = null
    },

    // Same mechanics as spend, kept separate because the two mean opposite
    // things to a reader: one number reached a record, the other never can.
    discard() {
      current = null
    },

    held() {
      return current
    },
  }
}
