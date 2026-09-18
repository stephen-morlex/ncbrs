import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AnnulRegistration } from './AnnulRegistration'

const get = vi.fn()
const post = vi.fn()

// One stable client object — the real useApiClient memoises, and a fresh mock
// per render re-runs the load effect and resets the form.
const client = { GET: get, POST: post }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

function ok<T>(data: T, status = 200) {
  return { data: { data }, error: undefined, response: { ok: true, status } }
}

function fail(status: number, body: unknown) {
  return { data: undefined, error: body, response: { ok: false, status } }
}

const user = () => userEvent.setup({ delay: null })

const record = {
  birthRecordId: '0199a1b2-0001-7000-8000-000000000001',
  brn: '100001',
  childFullName: 'Ayen Deng',
  dateOfBirth: '2026-01-05T00:00:00Z',
  sex: 'Female',
  status: 'Confirmed',
  annulment: null,
}

function respondRecord(value: unknown) {
  get.mockImplementation((url: string) => {
    if (url === '/api/BirthRecords/{brn}') {
      return Promise.resolve(value)
    }
    return Promise.resolve(ok({}))
  })
}

function renderScreen(brn = '100001') {
  render(
    <MemoryRouter initialEntries={[`/review/annulments?brn=${brn}`]}>
      <AnnulRegistration />
    </MemoryRouter>,
  )
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  respondRecord(ok(record))
})

describe('AnnulRegistration', () => {
  it('loads the record named in the URL and states the three-way distinction', async () => {
    renderScreen()

    expect(await screen.findByText('Ayen Deng')).toBeInTheDocument()
    // The distinction annulment turns on, so the wrong act is not reached for.
    expect(screen.getByText(/is annulment the right act/i)).toBeInTheDocument()
    expect(screen.getByText(/there was no such birth/i)).toBeInTheDocument()
  })

  it('will not open the confirmation without a justification', async () => {
    const typist = user()
    renderScreen()
    await screen.findByText('Ayen Deng')

    await typist.click(screen.getByRole('button', { name: /annul this registration/i }))

    expect(screen.getByText(/a justification is required/i)).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(post).not.toHaveBeenCalled()
  })

  it('annuls the registration and then shows it as void', async () => {
    post.mockResolvedValue(
      ok({
        brn: '100001',
        status: 'Annulled',
        reason: 'RegisteredInError',
        authorityReference: null,
        certificateRevoked: true,
        annulledAtUtc: '2026-09-18T09:00:00Z',
      }),
    )

    const typist = user()
    renderScreen()
    await screen.findByText('Ayen Deng')

    await typist.type(
      screen.getByLabelText(/justification/i),
      'Duplicate data-entry test row; no such birth occurred.',
    )
    await typist.click(screen.getByRole('button', { name: /annul this registration/i }))

    // A deliberate confirmation, restating that it cannot be undone.
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText(/cannot be undone/i)).toBeInTheDocument()
    await typist.click(within(dialog).getByRole('button', { name: /annul the registration/i }))

    expect(await screen.findByText(/this registration has been annulled/i)).toBeInTheDocument()
    expect(post.mock.calls[0][1].body.data.reason).toBe('RegisteredInError')
    expect(post.mock.calls[0][1].body.data.justification).toMatch(/no such birth/i)

    // The act is gone from the page — it cannot be done twice.
    expect(screen.queryByRole('button', { name: /annul this registration/i })).not.toBeInTheDocument()
  })

  it('shows an already-annulled record as void, with no way to annul again', async () => {
    respondRecord(
      ok({
        ...record,
        status: 'Annulled',
        annulment: {
          reason: 'CourtOrdered',
          justification: 'High Court order 44/2026.',
          authorityReference: 'HC-44-2026',
          annulledAtUtc: '2026-08-01T00:00:00Z',
        },
      }),
    )

    renderScreen()

    expect(await screen.findByText(/this registration has been annulled/i)).toBeInTheDocument()
    expect(screen.getByText(/HC-44-2026/)).toBeInTheDocument()
    expect(screen.getByText(/no un-annul/i)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /annul this registration/i })).not.toBeInTheDocument()
  })

  it('surfaces a not-found number and offers no annul form', async () => {
    respondRecord(fail(404, { status: 404, title: 'Not found', errors: [] }))
    renderScreen('999999')

    expect(await screen.findByText('Not found')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /annul this registration/i })).not.toBeInTheDocument()
  })
})
