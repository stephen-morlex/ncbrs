/**
 * How long a registration waited between being made on a device and reaching
 * the centre.
 *
 * For a village post that is how long it was without a link, and it is the
 * number that stops "received 3 October" on a birth in June being read as a
 * registration filed three months late. The statutory window was measured to
 * the device's clock precisely so that record is on time; a screen showing
 * only the arrival date invites the opposite conclusion.
 */
export function heldDays(
  registeredAtUtc: string | null | undefined,
  receivedAtUtc: string | null | undefined,
): number {
  if (!registeredAtUtc || !receivedAtUtc) {
    // Zero, not null, and the difference is carried elsewhere. A record
    // written before the registry kept the capture time genuinely cannot say,
    // but a badge is the wrong place for "unknown" -- the two dates on the
    // record say "Not recorded" in words, where it can be read plainly.
    return 0
  }

  const days = Math.floor((utcDay(receivedAtUtc) - utcDay(registeredAtUtc)) / MsPerDay)

  // Never negative. A device clock up to the skew tolerance ahead of the
  // server is accepted at registration, so the arrival can legitimately land
  // on the calendar day before the capture, and "held -1 days" is not a thing
  // to put in front of a registrar.
  return days > 0 ? days : 0
}

const MsPerDay = 24 * 60 * 60 * 1000

/**
 * Midnight UTC of the calendar day the timestamp falls on.
 *
 * Counted in whole days on the registry's own clock rather than the viewer's.
 * Subtracting the instants and dividing would make the answer depend on where
 * the person reading it is sitting, and this number appears next to a date of
 * birth, which is a calendar date and not an instant.
 */
function utcDay(value: string): number {
  const [date] = value.split('T')

  return Date.parse(`${date}T00:00:00Z`)
}
