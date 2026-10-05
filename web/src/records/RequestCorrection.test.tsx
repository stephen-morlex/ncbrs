import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { RequestCorrection } from './RequestCorrection'

const get = vi.fn()
const patch = vi.fn()

// One stable object, not a fresh one per render. The real useApiClient
// memoises for exactly this reason -- "so the client identity is stable across
// renders and does not retrigger effects that depend on it" -- and a mock that
// returns a new object each time re-runs the load effect on every render and
// resets the form under the person filling it in.
const client = { GET: get, PATCH: patch }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

function ok<T>(data: T) {
  return { data: { data }, error: undefined, response: { ok: true, status: 200 } }
}

/** 202 is a success, and nothing downstream may treat it as anything else. */
function accepted<T>(data: T) {
  return { data: { data }, error: undefined, response: { ok: true, status: 202 } }
}

const record = {
  birthRecordId: '0199a1b2-0001-7000-8000-000000000001',
  brn: '100001',
  childFullName: 'Ayen Deng',
  dateOfBirth: '2026-06-01T00:00:00Z',
  sex: 'Female',
  status: 'Confirmed',
  motherFullName: 'Nyandeng Deng',
  fatherFullName: null,
  birthWeightGrams: 3200,
  gestationalAgeWeeks: 39.5,
  birthOrder: 1,
}

/**
 * userEvent applies realistic typing delays by default, which pushes a form
 * with a sentence-long reason past the default 5s timeout. The delays buy
 * nothing here — what is under test is what the screen says, not how fast
 * anyone types.
 */
const user = () => userEvent.setup({ delay: null })

beforeEach(() => {
  get.mockReset()
  patch.mockReset()
  get.mockResolvedValue(ok(record))
})

async function loaded() {
  render(
    <MemoryRouter initialEntries={['/records/correct?brn=100001']}>
      <RequestCorrection />
    </MemoryRouter>,
  )

  await waitFor(() => expect(get).toHaveBeenCalled())
  await screen.findByLabelText(/birth weight/i)
}

async function correctTheWeight(typist: ReturnType<typeof user>, reason: string) {
  const weight = screen.getByLabelText(/birth weight/i)

  await typist.clear(weight)
  await typist.type(weight, '3350')
  await typist.type(screen.getByLabelText(/why is this/i), reason)
  await typist.click(screen.getByRole('button', { name: /submit the correction/i }))
}

describe('RequestCorrection', () => {
  it('shows what the register currently says', async () => {
    // A birth weight retyped from memory is not a correction, it is a second
    // guess. Five of the eight correctable fields were stored and published
    // nowhere until this change.
    await loaded()

    expect(screen.getByLabelText(/birth weight/i)).toHaveValue(3200)
    expect(screen.getByLabelText(/mother’s full name/i)).toHaveValue('Nyandeng Deng')
    expect(screen.getByLabelText(/child’s full name/i)).toHaveValue('Ayen Deng')
  })

  it('says which track a field is on before anything is submitted', async () => {
    // The ordering is the point. Afterwards would be too late — the registrar
    // has a family in front of them when they press save.
    await loaded()

    expect(screen.getByText(/takes effect immediately/i)).toBeInTheDocument()
    expect(screen.getByText(/needs a second registrar/i)).toBeInTheDocument()
  })

  it('will not submit an untouched form', async () => {
    // Prefilled, so submitting as-is would file a correction for every field
    // and drag each one onto the approval track for nothing.
    await loaded()

    expect(screen.getByRole('button', { name: /submit the correction/i })).toBeDisabled()
    expect(screen.getByText(/a correction names only what was wrong/i)).toBeInTheDocument()
  })

  it('still will not submit once a reason is given but nothing is changed', async () => {
    const typist = user()
    await loaded()

    await typist.type(screen.getByLabelText(/why is this/i), 'Clerical error on intake.')

    expect(screen.getByRole('button', { name: /submit the correction/i })).toBeDisabled()
  })

  it('sends only the field that changed', async () => {
    const typist = user()
    patch.mockResolvedValue(ok({ brn: '100001', applied: [], pendingApproval: [] }))

    await loaded()
    await correctTheWeight(typist, 'Scale recalibrated after intake.')

    await waitFor(() => expect(patch).toHaveBeenCalled())

    const [, options] = patch.mock.calls[0]

    expect(options.body.data.birthWeightGrams).toBe(3350)
    expect(options.body.data).not.toHaveProperty('childFullName')
    expect(options.body.data).not.toHaveProperty('motherFullName')
  })

  it('reports an immediate correction as already in effect', async () => {
    const typist = user()
    patch.mockResolvedValue(
      ok({
        brn: '100001',
        applied: [{ field: 'BirthWeightGrams', previousValue: '3200', newValue: '3350' }],
        pendingApproval: [],
        certificateInvalidated: false,
      }),
    )

    await loaded()
    await correctTheWeight(typist, 'Scale recalibrated.')

    expect(await screen.findByText(/already in effect/i)).toBeInTheDocument()
    expect(screen.queryByText(/waiting for a reviewer/i)).not.toBeInTheDocument()
  })

  it('does not let a 202 read as a failure, or as a success', async () => {
    // The sharpest case on this screen. 202 means something real happened and
    // the record has not changed — so neither a red error nor a plain green
    // tick tells the truth, and a registrar acting on either would mislead the
    // family standing in front of them.
    const typist = user()

    patch.mockResolvedValue(
      accepted({
        brn: '100001',
        applied: [],
        pendingApproval: [
          { field: 'ChildFullName', previousValue: 'Ayen Deng', newValue: 'Ayen Deng Lado' },
        ],
        certificateInvalidated: false,
      }),
    )

    await loaded()

    const name = screen.getByLabelText(/child’s full name/i)
    await typist.clear(name)
    await typist.type(name, 'Ayen Deng Lado')
    await typist.type(screen.getByLabelText(/why is this/i), 'Family name omitted at intake.')
    await typist.click(screen.getByRole('button', { name: /submit the correction/i }))

    expect(await screen.findByText(/waiting for a reviewer/i)).toBeInTheDocument()
    expect(screen.getByText(/The record has not changed/i)).toBeInTheDocument()
    expect(
      screen.getByText(/Do not tell the family the correction has been made/i),
    ).toBeInTheDocument()

    // And it is not reported as an error.
    expect(screen.queryByText(/could not|failed/i)).not.toBeInTheDocument()
  })

  it('reports a withdrawn certificate when an applied change invalidated one', async () => {
    const typist = user()
    patch.mockResolvedValue(
      ok({
        brn: '100001',
        applied: [{ field: 'Sex', previousValue: 'Female', newValue: 'Male' }],
        pendingApproval: [],
        certificateInvalidated: true,
      }),
    )

    await loaded()
    await correctTheWeight(typist, 'Recorded against the wrong chart.')

    expect(await screen.findByText(/certificate has been withdrawn/i)).toBeInTheDocument()
    expect(screen.getByText(/will fail verification/i)).toBeInTheDocument()
  })
})

describe('RequestCorrection — the fuller registration', () => {
  const fuller = {
    ...record,
    childFullName: 'Ayen Akol Deng',
    motherFullName: 'Achol Deng',
    details: {
      childGivenNames: 'Ayen Akol',
      childSurname: 'Deng',
      placeOfBirthKind: 'Home',
      placeOfBirth: 'Gumbo',
      mother: { givenNames: 'Achol', surname: 'Deng', occupation: 'Teacher', address: 'Gumbo, Juba' },
      father: null,
      marriage: null,
      proofOfAddress: null,
      restricted: false,
    },
  }

  beforeEach(() => {
    get.mockResolvedValue(ok(fuller))
    patch.mockResolvedValue(accepted({ brn: '100001', applied: [], pendingApproval: [] }))
  })

  async function submit(typist: ReturnType<typeof user>) {
    await typist.type(screen.getByLabelText(/why is this/i), 'Corrected from the antenatal card.')
    await typist.click(screen.getByRole('button', { name: /submit the correction/i }))
    await waitFor(() => expect(patch).toHaveBeenCalled())
    return patch.mock.calls[0][1].body.data
  }

  /** Corrected the way it was recorded: a name registered in parts is corrected in parts. */
  it('shows a name recorded in parts as its parts, and the details as recorded', async () => {
    await loaded()

    expect(screen.getByLabelText(/child’s given names/i)).toHaveValue('Ayen Akol')
    expect(screen.getByLabelText(/child’s surname/i)).toHaveValue('Deng')
    expect(screen.queryByLabelText(/child’s full name/i)).not.toBeInTheDocument()
    expect(screen.getByLabelText(/mother’s given names/i)).toHaveValue('Achol')
    expect(screen.getByLabelText(/mother’s job/i)).toHaveValue('Teacher')
    expect(screen.getByLabelText(/^where$/i)).toHaveValue('Gumbo')
  })

  it('sends a parent’s changed part alone, nested as the request has it', async () => {
    const typist = user()
    await loaded()

    const job = screen.getByLabelText(/mother’s job/i)
    await typist.clear(job)
    await typist.type(job, 'Nurse')

    const data = await submit(typist)
    expect(data.mother).toEqual({ occupation: 'Nurse' })
    expect(data.childFullName).toBeUndefined()
    expect(data.childGivenNames).toBeUndefined()
  })

  /** An emptied box removes what it said; the registry reads "" as that. */
  it('sends an emptied box as empty, to remove what it said', async () => {
    const typist = user()
    await loaded()

    await typist.clear(screen.getByLabelText(/mother’s address/i))

    const data = await submit(typist)
    expect(data.mother).toEqual({ address: '' })
  })

  it('sends the child’s corrected surname, not a full name', async () => {
    const typist = user()
    await loaded()

    const surname = screen.getByLabelText(/child’s surname/i)
    await typist.clear(surname)
    await typist.type(surname, 'Deeng')

    const data = await submit(typist)
    expect(data.childSurname).toBe('Deeng')
    expect(data.childFullName).toBeUndefined()
  })

  it('says the details are corrected after review, like the names', async () => {
    const typist = user()
    patch.mockResolvedValue(
      accepted({
        brn: '100001',
        applied: [],
        pendingApproval: [{ field: 'Mother.Occupation', previousValue: 'Teacher', newValue: 'Nurse' }],
      }),
    )
    await loaded()

    const job = screen.getByLabelText(/mother’s job/i)
    await typist.clear(job)
    await typist.type(job, 'Nurse')
    await submit(typist)

    expect(await screen.findByText(/waiting for a\s+reviewer/i)).toBeInTheDocument()
    // Named as a registrar reads it, not as the registry stores it.
    expect(screen.getByText('Mother’s job')).toBeInTheDocument()
  })

  /** Shown to those who may act for the facility only; another sees that they were withheld. */
  it('does not offer a withheld address for editing', async () => {
    get.mockResolvedValue(ok({ ...fuller, details: { ...fuller.details, restricted: true, mother: { ...fuller.details.mother, address: null } } }))
    await loaded()

    expect(screen.getByLabelText(/mother’s address/i)).toBeDisabled()
    expect(screen.getByLabelText(/mother’s document number/i)).toBeDisabled()
  })
})
