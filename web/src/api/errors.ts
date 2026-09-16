import type { components } from '@/api/generated/api'

type ApiErrorResponse = components['schemas']['ApiErrorResponse']
type ApiFieldError = components['schemas']['ApiError']

/**
 * Turns whatever came back into something a screen can render.
 *
 * The API answers failures with `{ status, title, errors: [{ field, message }] }`
 * — the field paths are the point. A UI that collapses that into "something
 * went wrong" throws away the server's best work and leaves a registrar
 * rereading a twenty-field form looking for the one the server already
 * named.
 *
 * Central because there is exactly one wire format and it should be
 * understood in exactly one place. Written defensively because the one thing
 * this must never do is throw: it runs on the failure path, and an error
 * handler that fails replaces a useful message with a blank screen.
 */

export interface NcbrsError {
  /** The server's summary, or a plain description when there was no response. */
  title: string

  /** HTTP status, or null when the request never reached the server. */
  status: number | null

  /** Per-field problems, in the order the server listed them. */
  fields: ApiFieldError[]

  /**
   * True when nothing was reached at all — offline, DNS, a refused CORS
   * preflight. Worth separating: "the server said no" and "the server did
   * not answer" need different words and different advice, and conflating
   * them is how a user retries a request that will never succeed.
   */
  unreachable: boolean

  /** The underlying cause, when there is one worth showing. */
  detail?: string
}

function isFieldError(value: unknown): value is ApiFieldError {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as ApiFieldError).field === 'string' &&
    typeof (value as ApiFieldError).message === 'string'
  )
}

/**
 * The document says `integer`, and the API sends one. The string branch is
 * kept anyway because this runs on the failure path: a body that is already
 * not what was expected is the worst place to be strict about its shape.
 */
function readStatus(value: unknown, fallback: number | null): number | null {
  if (typeof value === 'number') {
    return value
  }

  if (typeof value === 'string' && /^-?\d+$/.test(value)) {
    return Number(value)
  }

  return fallback
}

/** A default that says what a reader can do, per status. */
function titleFor(status: number | null): string {
  switch (status) {
    case 400:
      return 'The request was not accepted'
    case 401:
      return 'Your session has expired'
    case 403:
      return 'This is not available to your account'
    case 404:
      return 'Not found'
    case 409:
      return 'This conflicts with the current record'
    default:
      return status === null ? 'Could not reach the server' : 'Something went wrong'
  }
}

export function toNcbrsError(body: unknown, status: number | null): NcbrsError {
  const envelope = body as { data?: unknown } | undefined

  // Errors travel in the same { meta, data } envelope as everything else, so
  // the body may be the error or may wrap it. Accept both rather than
  // depending on which layer unwrapped it.
  const candidate = (
    typeof envelope?.data === 'object' && envelope.data !== null ? envelope.data : body
  ) as Partial<ApiErrorResponse> | undefined

  const fields = Array.isArray(candidate?.errors) ? candidate.errors.filter(isFieldError) : []
  const resolved = readStatus(candidate?.status, status)

  return {
    title:
      typeof candidate?.title === 'string' && candidate.title.length > 0
        ? candidate.title
        : titleFor(resolved),
    status: resolved,
    fields,
    unreachable: false,
  }
}

/** For a request that never got a response at all. */
export function unreachableError(cause?: unknown): NcbrsError {
  return {
    title: titleFor(null),
    status: null,
    fields: [],
    unreachable: true,
    ...(cause instanceof Error && cause.message ? { detail: cause.message } : {}),
  }
}

/**
 * The messages for one field, matched case-insensitively.
 *
 * The API names fields as they appear in the request body — `child.firstName`
 * — while a form control is usually registered under the same path in a
 * different case. Matching exactly would silently attach nothing, which
 * looks identical to the server not having complained.
 */
export function messagesFor(error: NcbrsError | null, field: string): string[] {
  if (!error) {
    return []
  }

  const wanted = field.toLowerCase()

  return error.fields.filter((item) => item.field.toLowerCase() === wanted).map((item) => item.message)
}

/**
 * Problems that name no field, or name one this form does not show.
 *
 * Without this a cross-field rule — "antenatal care began before the
 * pregnancy" — would be attached to nothing and vanish, and the user would
 * see a rejected form with every field apparently fine.
 */
export function unattachedMessages(error: NcbrsError | null, shown: readonly string[]): string[] {
  if (!error) {
    return []
  }

  const known = new Set(shown.map((field) => field.toLowerCase()))

  return error.fields
    .filter((item) => item.field.length === 0 || !known.has(item.field.toLowerCase()))
    .map((item) => (item.field ? `${item.field}: ${item.message}` : item.message))
}
