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

// Stands in for a token renewal. The real client is memoised on the auth
// object, so a renewed session is a *new* client -- and anything that kept the
// old one keeps presenting the old token after it expires.
let renewed: typeof client | null = null

vi.mock('@/api/useApi', () => ({
  useApiClient: () => renewed ?? client,
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
  renewed = null

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
  return render(
    <MemoryRouter>
      <RegisterBirth />
    </MemoryRouter>,
  )
}

async function setDateOfBirth(value: string) {
  const input = screen.getByLabelText(/^date of birth$/i)

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
    expect(summary.textContent).toMatch(/the child’s given names/i)
    expect(summary.textContent).toMatch(/the child’s surname/i)
    expect(summary.textContent).toMatch(/date of birth/i)
    expect(summary.textContent).toMatch(/place of birth/i)
    expect(summary.textContent).not.toMatch(/childGivenNames|placeOfBirthKind/)
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

    await typist.type(screen.getByLabelText(/child’s given names/i), 'Ayen')
    await typist.type(screen.getByLabelText(/child’s surname/i), 'Deng')
    await typist.click(screen.getByLabelText(/^place of birth$/i))
    await typist.click(await screen.findByRole('option', { name: /at this facility/i }))
    fireEvent.change(screen.getByLabelText(/^date of birth$/i), { target: { value: daysAgo(3) } })

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

    await typist.type(screen.getByLabelText(/child’s given names/i), 'Ayen')
    await typist.type(screen.getByLabelText(/child’s surname/i), 'Deng')
    await typist.click(screen.getByLabelText(/^place of birth$/i))
    await typist.click(await screen.findByRole('option', { name: /at this facility/i }))
    fireEvent.change(screen.getByLabelText(/^date of birth$/i), { target: { value: daysAgo(3) } })
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

    await typist.type(screen.getByLabelText(/child’s given names/i), 'Ayen')
    await typist.type(screen.getByLabelText(/child’s surname/i), 'Deng')
    await typist.click(screen.getByLabelText(/^place of birth$/i))
    await typist.click(await screen.findByRole('option', { name: /at this facility/i }))
    fireEvent.change(screen.getByLabelText(/^date of birth$/i), { target: { value: daysAgo(3) } })
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
    await typist.type(screen.getByLabelText(/child’s given names/i), 'Ayen')
    await typist.type(screen.getByLabelText(/child’s surname/i), 'Deng')
    await typist.click(screen.getByLabelText(/^place of birth$/i))
    await typist.click(await screen.findByRole('option', { name: /at this facility/i }))
    fireEvent.change(screen.getByLabelText(/^date of birth$/i), { target: { value: daysAgo(3) } })
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

  /**
   * The draw is kept in a ref across attempts, because it holds the number
   * this registration has reserved. It must still ask for a *new* number
   * through the session as it is now: if it kept the client from the first
   * attempt, a registrar who fixed a refused form after their token renewed
   * would have the next draw sent with the expired token and refused as
   * unauthorised.
   */
  it('draws the next number through the current session, not the one the form opened with', async () => {
    let drawn = 200000
    const refuseAsTaken = () =>
      Promise.resolve({
        data: undefined,
        error: {
          status: 409,
          title: 'BRN already registered.',
          errors: [{ field: 'data.brn', message: "BRN '200000' has already been registered." }],
        },
        response: { ok: false, status: 409 },
      })

    post.mockImplementation((path: string) =>
      path.includes('request-brn-block')
        ? Promise.resolve(ok({ blockStart: drawn++, blockEnd: drawn }))
        : refuseAsTaken(),
    )

    const typist = userEvent.setup({ delay: null })

    const view = show()
    await waitFor(() => expect(get).toHaveBeenCalled())
    await fillIn(typist)

    const save = screen.getByRole('button', { name: /register the birth/i })

    await typist.click(save)
    expect(await screen.findByText(/already been registered/i)).toBeInTheDocument()

    // The session renews while the registrar corrects the form. A real
    // renewal re-renders every auth consumer; typing would not, because the
    // form's inputs are uncontrolled -- so re-render explicitly.
    const renewedPost = vi.fn((path: string) =>
      path.includes('request-brn-block')
        ? Promise.resolve(ok({ blockStart: drawn++, blockEnd: drawn }))
        : refuseAsTaken(),
    )
    renewed = { GET: get, POST: renewedPost }
    view.rerender(
      <MemoryRouter>
        <RegisterBirth />
      </MemoryRouter>,
    )

    await typist.click(save)

    await waitFor(() =>
      expect(renewedPost.mock.calls.some((call: unknown[]) => String(call[0]).includes('request-brn-block'))).toBe(true),
    )
    expect(post.mock.calls.filter((call: unknown[]) => String(call[0]).includes('request-brn-block'))).toHaveLength(1)
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

describe('the fuller registration', () => {
  function accepting() {
    post.mockImplementation((path: string) =>
      path.includes('request-brn-block')
        ? Promise.resolve(ok({ blockStart: 100001, blockEnd: 100001 }))
        : Promise.resolve(ok({ brn: '100001' })),
    )
  }

  async function fillRequired(typist: ReturnType<typeof userEvent.setup>, place = /at this facility/i) {
    await typist.type(screen.getByLabelText(/child’s given names/i), 'Ayen Akol')
    await typist.type(screen.getByLabelText(/child’s surname/i), 'Deng')
    fireEvent.change(screen.getByLabelText(/^date of birth$/i), { target: { value: daysAgo(3) } })
    await typist.click(screen.getByLabelText(/^place of birth$/i))
    await typist.click(await screen.findByRole('option', { name: place }))
    await typist.click(screen.getByLabelText(/facility/i))
    await typist.click(await screen.findByRole('option', { name: /terekeka/i }))
    await typist.click(screen.getByLabelText(/^sex$/i))
    await typist.click(await screen.findByRole('option', { name: /female/i }))
    await typist.click(screen.getByLabelText(/plurality/i))
    await typist.click(await screen.findByRole('option', { name: /singleton/i }))
  }

  function registered() {
    const call = post.mock.calls.find((entry: unknown[]) => String(entry[0]) === '/api/BirthRecords/register')
    return call?.[1].body.data
  }

  it('registers with only the required fields, sending none of the optional groups', async () => {
    accepting()
    const typist = userEvent.setup({ delay: null })

    show()
    await waitFor(() => expect(get).toHaveBeenCalled())
    await fillRequired(typist)
    await typist.click(screen.getByRole('button', { name: /register the birth/i }))

    await waitFor(() => expect(registered()).toBeDefined())
    const body = registered()

    expect(body.childGivenNames).toBe('Ayen Akol')
    expect(body.childSurname).toBe('Deng')
    expect(body.placeOfBirthKind).toBe('ThisFacility')
    expect(body.placeOfBirth).toBeUndefined()
    // The one-piece name is the older tablets' form; sending both is refused.
    expect(body.childFullName).toBeUndefined()
    expect(body.mother).toBeUndefined()
    expect(body.father).toBeUndefined()
    expect(body.marriage).toBeUndefined()
    expect(body.proofOfAddress).toBeUndefined()
  })

  it('keeps the optional sections closed until asked for', async () => {
    show()
    await waitFor(() => expect(get).toHaveBeenCalled())

    expect(screen.queryByLabelText(/mother’s given names/i)).not.toBeInTheDocument()
    expect(screen.queryByLabelText(/marriage certificate number/i)).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: /add the mother/i }))

    expect(await screen.findByLabelText(/mother’s given names/i)).toBeInTheDocument()
    expect(screen.getByLabelText(/mother’s maiden surname/i)).toBeInTheDocument()
  })

  it('asks the father for no maiden surname', async () => {
    show()
    await waitFor(() => expect(get).toHaveBeenCalled())

    fireEvent.click(screen.getByRole('button', { name: /add the father/i }))

    expect(await screen.findByLabelText(/father’s given names/i)).toBeInTheDocument()
    expect(screen.queryByLabelText(/maiden surname/i)).not.toBeInTheDocument()
  })

  it('needs to know where, when the birth was not at this facility', async () => {
    const typist = userEvent.setup({ delay: null })

    show()
    await waitFor(() => expect(get).toHaveBeenCalled())
    await fillRequired(typist, /at home/i)
    await typist.click(screen.getByRole('button', { name: /register the birth/i }))

    expect(await screen.findByText(/say where: the facility, village or place/i)).toBeInTheDocument()
    expect(post).not.toHaveBeenCalled()

    await typist.type(screen.getByLabelText(/^where$/i), 'Gumbo, near the borehole')
    accepting()
    await typist.click(screen.getByRole('button', { name: /register the birth/i }))

    await waitFor(() => expect(registered()).toBeDefined())
    expect(registered().placeOfBirthKind).toBe('Home')
    expect(registered().placeOfBirth).toBe('Gumbo, near the borehole')
  })

  it('sends a parent with only what was given', async () => {
    accepting()
    const typist = userEvent.setup({ delay: null })

    show()
    await waitFor(() => expect(get).toHaveBeenCalled())
    await fillRequired(typist)

    await typist.click(screen.getByRole('button', { name: /add the mother/i }))
    await typist.type(await screen.findByLabelText(/mother’s given names/i), 'Achol')
    await typist.type(screen.getByLabelText(/mother’s maiden surname/i), 'Garang')
    await typist.type(screen.getByLabelText(/mother’s job/i), 'Teacher')
    await typist.click(screen.getByLabelText(/mother’s identity document/i))
    await typist.click(await screen.findByRole('option', { name: /national id/i }))
    await typist.type(screen.getByLabelText(/mother’s document number/i), 'SS1234567')

    await typist.click(screen.getByRole('button', { name: /register the birth/i }))

    await waitFor(() => expect(registered()).toBeDefined())
    expect(registered().mother).toEqual({
      givenNames: 'Achol',
      maidenSurname: 'Garang',
      occupation: 'Teacher',
      documentType: 'NationalId',
      documentNumber: 'SS1234567',
      surname: undefined,
      dateOfBirth: undefined,
      placeOfBirth: undefined,
      address: undefined,
    })
    expect(registered().father).toBeUndefined()
  })

  it('refuses a parent’s details that do not name them, and opens the section to say so', async () => {
    const typist = userEvent.setup({ delay: null })

    show()
    await waitFor(() => expect(get).toHaveBeenCalled())
    await fillRequired(typist)

    await typist.click(screen.getByRole('button', { name: /add the father/i }))
    await typist.type(await screen.findByLabelText(/father’s job/i), 'Cattle keeper')
    // Closed again before submitting: what is wrong inside must still show.
    await typist.click(screen.getByRole('button', { name: /hide the father/i }))
    expect(screen.queryByLabelText(/father’s job/i)).not.toBeInTheDocument()

    await typist.click(screen.getByRole('button', { name: /register the birth/i }))

    expect(await screen.findByText(/give the father’s given names or surname/i)).toBeInTheDocument()
    expect(screen.getByLabelText(/father’s job/i)).toBeInTheDocument()
    expect(post).not.toHaveBeenCalled()
  })

  it('records a document as its type and number together', async () => {
    const typist = userEvent.setup({ delay: null })

    show()
    await waitFor(() => expect(get).toHaveBeenCalled())
    await fillRequired(typist)

    await typist.click(screen.getByRole('button', { name: /add the mother/i }))
    await typist.type(await screen.findByLabelText(/mother’s given names/i), 'Achol')
    await typist.type(screen.getByLabelText(/mother’s document number/i), 'SS1234567')

    await typist.click(screen.getByRole('button', { name: /register the birth/i }))

    expect(await screen.findByText(/say which document this is/i)).toBeInTheDocument()
    expect(post).not.toHaveBeenCalled()
  })

  it('sends the marriage and proof of address when given', async () => {
    accepting()
    const typist = userEvent.setup({ delay: null })

    show()
    await waitFor(() => expect(get).toHaveBeenCalled())
    await fillRequired(typist)

    await typist.click(screen.getByRole('button', { name: /add the parents’ marriage/i }))
    fireEvent.change(await screen.findByLabelText(/date of the parents’ marriage/i), {
      target: { value: '2023-01-14' },
    })
    await typist.click(screen.getByRole('button', { name: /add proof of address/i }))
    await typist.type(await screen.findByLabelText(/what was shown/i), 'Utility bill')

    await typist.click(screen.getByRole('button', { name: /register the birth/i }))

    await waitFor(() => expect(registered()).toBeDefined())
    expect(registered().marriage).toEqual({ date: '2023-01-14', certificateNumber: undefined })
    expect(registered().proofOfAddress).toEqual({ kind: 'Utility bill', reference: undefined })
  })
})
