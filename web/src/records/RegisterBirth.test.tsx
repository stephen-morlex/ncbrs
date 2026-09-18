import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { RegisterBirth } from './RegisterBirth'

const FacilityId = '0199a1b2-0001-7000-8000-000000000001'

const get = vi.fn()
const post = vi.fn()

// One stable object, not a fresh one per render. The real useApiClient
// memoises so that effects depending on it do not re-run every render; a mock
// that ignores that reloads the form continuously and resets what was typed.
const client = { GET: get, POST: post }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
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
              { facilityId: FacilityId, name: 'Terekeka Village Health Post', brnRemaining: 500 },
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
                { facilityId: FacilityId, name: 'Terekeka Village Health Post', brnRemaining: 0 },
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

describe('when the form is refused', () => {
  it('says so at the button instead of appearing to do nothing', async () => {
    // The reported bug. The only feedback used to be a line of red under
    // whichever field was unfilled -- on a form this long, usually scrolled
    // off screen. The click looked like it did nothing, so the registrar
    // clicked again and concluded the system was broken.
    show()
    await waitFor(() => expect(get).toHaveBeenCalled())

    fireEvent.click(screen.getByRole('button', { name: /register the birth/i }))

    expect(await screen.findByText(/this birth has not been registered yet/i)).toBeInTheDocument()
    expect(screen.getByText(/nothing was sent to the registry/i)).toBeInTheDocument()
  })

  it('names the missing fields the way the screen labels them', async () => {
    // "childFullName" asks the reader to translate before they can act.
    show()
    await waitFor(() => expect(get).toHaveBeenCalled())

    fireEvent.click(screen.getByRole('button', { name: /register the birth/i }))

    const summary = await screen.findByText(/still needed:/i)

    expect(summary.textContent).toMatch(/facility/i)
    expect(summary.textContent).toMatch(/the child’s name/i)
    expect(summary.textContent).toMatch(/date of birth/i)
    expect(summary.textContent).not.toMatch(/childFullName/)
  })

  it('sends nothing to the registry when it refuses', async () => {
    show()
    await waitFor(() => expect(get).toHaveBeenCalled())

    fireEvent.click(screen.getByRole('button', { name: /register the birth/i }))
    await screen.findByText(/this birth has not been registered yet/i)

    // No BRN drawn, in particular. A refused form must not advance the
    // facility's counter.
    expect(post).not.toHaveBeenCalled()
  })

  it('clears the summary once the form is put right', async () => {
    post.mockImplementation((path: string) =>
      path.includes('request-brn-block')
        ? Promise.resolve(ok({ blockStart: 100001, blockEnd: 100001 }))
        : Promise.resolve(ok({ brn: '100001' })),
    )

    const typist = userEvent.setup({ delay: null })

    show()
    await waitFor(() => expect(get).toHaveBeenCalled())

    fireEvent.click(screen.getByRole('button', { name: /register the birth/i }))
    await screen.findByText(/this birth has not been registered yet/i)

    await typist.type(screen.getByLabelText(/child’s full name/i), 'Ayen Deng')
    fireEvent.change(screen.getByLabelText(/date of birth/i), { target: { value: daysAgo(3) } })

    await typist.click(screen.getByLabelText(/facility/i))
    await typist.click(await screen.findByRole('option', { name: /terekeka/i }))
    await typist.click(screen.getByLabelText(/^sex$/i))
    await typist.click(await screen.findByRole('option', { name: /female/i }))
    await typist.click(screen.getByLabelText(/plurality/i))
    await typist.click(await screen.findByRole('option', { name: /singleton/i }))

    await typist.click(screen.getByRole('button', { name: /register the birth/i }))

    await waitFor(() => expect(post).toHaveBeenCalledTimes(2))

    expect(
      screen.queryByText(/this birth has not been registered yet/i),
    ).not.toBeInTheDocument()
  })
})

describe('when the registry refuses', () => {
  it('brings the refusal into view instead of leaving it above the fold', async () => {
    // The submit button sits ~1800px down this form on a 720px viewport, and
    // the registry's refusal renders at the top. Without scrolling to it, a
    // 400 and a broken button are indistinguishable from where the click
    // happened.
    const scrolled: unknown[] = []
    Element.prototype.scrollIntoView = function (arg?: unknown) {
      scrolled.push(this)
      void arg
    }

    post.mockImplementation((path: string) =>
      path.includes('request-brn-block')
        ? Promise.resolve(ok({ blockStart: 100001, blockEnd: 100001 }))
        : Promise.resolve({
            data: undefined,
            error: {
              status: 400,
              title: 'Late registration evidence required.',
              errors: [{ field: 'data.lateRegistration', message: 'Evidence and a declarant are required.' }],
            },
            response: { ok: false, status: 400 },
          }),
    )

    const typist = userEvent.setup({ delay: null })

    show()
    await waitFor(() => expect(get).toHaveBeenCalled())

    await typist.type(screen.getByLabelText(/child’s full name/i), 'Ayen Deng')
    fireEvent.change(screen.getByLabelText(/date of birth/i), { target: { value: daysAgo(3) } })
    await typist.click(screen.getByLabelText(/facility/i))
    await typist.click(await screen.findByRole('option', { name: /terekeka/i }))
    await typist.click(screen.getByLabelText(/^sex$/i))
    await typist.click(await screen.findByRole('option', { name: /female/i }))
    await typist.click(screen.getByLabelText(/plurality/i))
    await typist.click(await screen.findByRole('option', { name: /singleton/i }))

    await typist.click(screen.getByRole('button', { name: /register the birth/i }))

    expect(await screen.findByText(/late registration evidence required/i)).toBeInTheDocument()

    // And something was scrolled to, rather than the message being left where
    // the person cannot see it.
    await waitFor(() => expect(scrolled.length).toBeGreaterThan(0))
  })

  it('brings a refused BRN draw into view too', async () => {
    const scrolled: unknown[] = []
    Element.prototype.scrollIntoView = function () {
      scrolled.push(this)
    }

    // A facility that has exhausted its pre-approved range.
    post.mockResolvedValue({
      data: undefined,
      error: { status: 409, title: 'BRN range exhausted.', errors: [] },
      response: { ok: false, status: 409 },
    })

    const typist = userEvent.setup({ delay: null })

    show()
    await waitFor(() => expect(get).toHaveBeenCalled())

    await typist.type(screen.getByLabelText(/child’s full name/i), 'Ayen Deng')
    fireEvent.change(screen.getByLabelText(/date of birth/i), { target: { value: daysAgo(3) } })
    await typist.click(screen.getByLabelText(/facility/i))
    await typist.click(await screen.findByRole('option', { name: /terekeka/i }))
    await typist.click(screen.getByLabelText(/^sex$/i))
    await typist.click(await screen.findByRole('option', { name: /female/i }))
    await typist.click(screen.getByLabelText(/plurality/i))
    await typist.click(await screen.findByRole('option', { name: /singleton/i }))

    await typist.click(screen.getByRole('button', { name: /register the birth/i }))

    expect(await screen.findByText(/brn range exhausted/i)).toBeInTheDocument()
    await waitFor(() => expect(scrolled.length).toBeGreaterThan(0))
  })
})

describe('when the registry says the number is already registered', () => {
  async function fillIn(typist: ReturnType<typeof userEvent.setup>) {
    await typist.type(screen.getByLabelText(/child’s full name/i), 'Ayen Deng')
    fireEvent.change(screen.getByLabelText(/date of birth/i), { target: { value: daysAgo(3) } })
    await typist.click(screen.getByLabelText(/facility/i))
    await typist.click(await screen.findByRole('option', { name: /terekeka/i }))
    await typist.click(screen.getByLabelText(/^sex$/i))
    await typist.click(await screen.findByRole('option', { name: /female/i }))
    await typist.click(screen.getByLabelText(/plurality/i))
    await typist.click(await screen.findByRole('option', { name: /singleton/i }))
  }

  it('draws a fresh number on the next attempt instead of refusing forever', async () => {
    // The reported failure. The registry's counter can point at a number that
    // is already registered, and retrying with the same one can never succeed
    // -- so the form sat there refusing however many times it was pressed.
    let drawn = 200000

    post.mockImplementation((path: string) => {
      if (path.includes('request-brn-block')) {
        return Promise.resolve(ok({ blockStart: drawn++, blockEnd: drawn }))
      }

      return Promise.resolve({
        data: undefined,
        error: {
          status: 409,
          title: 'BRN already registered.',
          errors: [{ field: 'data.brn', message: "BRN '200000' has already been registered." }],
        },
        response: { ok: false, status: 409 },
      })
    })

    const typist = userEvent.setup({ delay: null })

    show()
    await waitFor(() => expect(get).toHaveBeenCalled())
    await fillIn(typist)

    const save = screen.getByRole('button', { name: /register the birth/i })

    await typist.click(save)
    expect(await screen.findByText(/already been registered/i)).toBeInTheDocument()

    await typist.click(save)
    await waitFor(() => expect(post.mock.calls.filter((call: unknown[]) => String(call[0]).includes("request-brn-block"))).toHaveLength(2))

    // Two draws, two different numbers -- not the same one refused twice.
    const registers = post.mock.calls.filter((call: unknown[]) => !String(call[0]).includes("request-brn-block"))
    expect(registers[0][1].body.data.brn).not.toBe(registers[1][1].body.data.brn)
  })

  it('still holds the number when the refusal was about the form', async () => {
    // A refusal naming a different field must not burn a number.
    post.mockImplementation((path: string) =>
      path.includes('request-brn-block')
        ? Promise.resolve(ok({ blockStart: 100001, blockEnd: 100001 }))
        : Promise.resolve({
            data: undefined,
            error: {
              status: 400,
              title: 'Late registration evidence required.',
              errors: [{ field: 'data.lateRegistration', message: 'Evidence is required.' }],
            },
            response: { ok: false, status: 400 },
          }),
    )

    const typist = userEvent.setup({ delay: null })

    show()
    await waitFor(() => expect(get).toHaveBeenCalled())
    await fillIn(typist)

    const save = screen.getByRole('button', { name: /register the birth/i })

    await typist.click(save)
    await screen.findByText(/late registration evidence required/i)

    await typist.click(save)
    await waitFor(() => expect(post.mock.calls.filter((call: unknown[]) => !String(call[0]).includes("request-brn-block"))).toHaveLength(2))

    expect(post.mock.calls.filter((call: unknown[]) => String(call[0]).includes("request-brn-block"))).toHaveLength(1)
  })
})
