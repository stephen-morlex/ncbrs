import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { RegisterBirth } from './RegisterBirth'

const FacilityId = '0199a1b2-0001-7000-8000-000000000001'

const get = vi.fn()
const post = vi.fn()

vi.mock('@/api/useApi', () => ({
  useApiClient: () => ({ GET: get, POST: post }),
}))

function ok<T>(data: T) {
  return { data: { data }, error: undefined, response: { ok: true, status: 200 } }
}

function daysAgo(days: number): string {
  return new Date(Date.now() - days * 24 * 60 * 60 * 1000).toISOString().slice(0, 10)
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()

  get.mockImplementation((path: string) =>
    path === '/api/facilities'
      ? Promise.resolve(
          ok({
            items: [
              { facilityId: FacilityId, name: 'Kabwe Village Health Post', brnRemaining: 500 },
            ],
          }),
        )
      : // The window comes from the server, never from a constant here: it is
        // set in law, and a copy in the client would keep answering
        // confidently after the Act changed.
        Promise.resolve(ok({ statutoryWindowDays: 90 })),
  )
})

function show() {
  render(
    <MemoryRouter>
      <RegisterBirth />
    </MemoryRouter>,
  )
}

async function setDateOfBirth(value: string) {
  const input = screen.getByLabelText(/date of birth/i)

  fireEvent.change(input, { target: { value } })

  await waitFor(() => expect((input as HTMLInputElement).value).toBe(value))
}

describe('RegisterBirth', () => {
  it('reads the statutory window from the registry rather than assuming one', async () => {
    show()

    await waitFor(() =>
      expect(get).toHaveBeenCalledWith('/api/BirthRecords/registration-rules', expect.anything()),
    )

    expect(
      await screen.findByText(/more than 90 days after they happened need supporting evidence/i),
    ).toBeInTheDocument()
  })

  it('asks for nothing extra when the birth is inside the window', async () => {
    show()
    await waitFor(() => expect(get).toHaveBeenCalled())

    await setDateOfBirth(daysAgo(10))

    expect(screen.queryByText(/outside the statutory window/i)).not.toBeInTheDocument()
    expect(screen.queryByLabelText(/declarant/i)).not.toBeInTheDocument()
  })

  it('asks for evidence and a declarant once the birth falls outside the window', async () => {
    show()
    await waitFor(() => expect(get).toHaveBeenCalled())

    await setDateOfBirth(daysAgo(400))

    expect(await screen.findByText(/outside the statutory window/i)).toBeInTheDocument()
    expect(screen.getByLabelText(/^declarant$/i)).toBeInTheDocument()
    expect(screen.getByLabelText(/supporting evidence/i)).toBeInTheDocument()
  })

  it('says the registration stands and only the certificate waits', async () => {
    // Late registration is the ordinary route for a large share of rural
    // births. A registrar must not read this as a refusal and turn a family
    // away -- the child gets a registration number either way.
    show()
    await waitFor(() => expect(get).toHaveBeenCalled())

    await setDateOfBirth(daysAgo(400))

    expect(
      await screen.findByText(/the child will have a registration number/i),
    ).toBeInTheDocument()
    expect(screen.getByText(/certificate waits until a district registrar/i)).toBeInTheDocument()
  })

  it('stops asking again if the date is corrected back inside the window', async () => {
    // A mistyped year is the commonest way this section appears by accident,
    // and evidence supplied for an on-time birth is refused by the API rather
    // than ignored.
    show()
    await waitFor(() => expect(get).toHaveBeenCalled())

    await setDateOfBirth(daysAgo(400))
    expect(await screen.findByText(/outside the statutory window/i)).toBeInTheDocument()

    await setDateOfBirth(daysAgo(10))

    await waitFor(() =>
      expect(screen.queryByText(/outside the statutory window/i)).not.toBeInTheDocument(),
    )
  })

  it('warns before anything is typed when the facility has no numbers left', async () => {
    get.mockImplementation((path: string) =>
      path === '/api/facilities'
        ? Promise.resolve(
            ok({
              items: [
                { facilityId: FacilityId, name: 'Kabwe Village Health Post', brnRemaining: 0 },
              ],
            }),
          )
        : Promise.resolve(ok({ statutoryWindowDays: 90 })),
    )

    show()
    await waitFor(() => expect(get).toHaveBeenCalled())

    // Nothing is selected yet, so nothing is claimed yet either.
    expect(screen.queryByText(/no registration numbers left/i)).not.toBeInTheDocument()
  })
})
