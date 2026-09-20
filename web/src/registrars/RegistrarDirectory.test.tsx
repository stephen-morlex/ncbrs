import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { RegistrarDirectory } from './RegistrarDirectory'

const get = vi.fn()

// One stable client object — the real useApiClient memoises, and a fresh mock
// per render re-runs the load effect and resets the list.
const client = { GET: get }

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

const registrar = {
  registrarId: '0199a1b2-reg0-7000-8000-000000000001',
  displayName: 'Nyandeng Lado',
  role: 'DistrictOfficer',
  facilityId: '0199a1b2-fac0-7000-8000-000000000001',
  facilityName: 'Juba Central Clinic',
  countyCode: 'SS0101',
}

function page(items: unknown[], nextCursor: string | null = null) {
  return { items, total: items.length, nextCursor }
}

// The last registrars query the mock was asked to answer, so a test can assert
// what the search sent.
let lastRegistrarsQuery: Record<string, unknown> | undefined

function respondWith(registrars: ReturnType<typeof ok> | ReturnType<typeof fail>) {
  lastRegistrarsQuery = undefined
  get.mockImplementation((url: string, opts?: { params?: { query?: Record<string, unknown> } }) => {
    if (url === '/api/facilities') {
      return Promise.resolve(ok(page([])))
    }
    if (url === '/api/registrars') {
      lastRegistrarsQuery = opts?.params?.query
      return Promise.resolve(registrars)
    }
    return Promise.resolve(ok({}))
  })
}

function renderDirectory() {
  render(
    <MemoryRouter initialEntries={['/registrars']}>
      <RegistrarDirectory />
    </MemoryRouter>,
  )
}

beforeEach(() => {
  get.mockReset()
  respondWith(ok(page([registrar])))
})

describe('RegistrarDirectory', () => {
  it('lists registrars with their role, facility and district', async () => {
    renderDirectory()

    expect(await screen.findByText('Nyandeng Lado')).toBeInTheDocument()
    // The role reads as words, not the enum token.
    expect(screen.getByText('District officer')).toBeInTheDocument()
    expect(screen.getByText('Juba Central Clinic')).toBeInTheDocument()
    expect(screen.getByText('SS0101')).toBeInTheDocument()
  })

  it('searches by name only on submit, and sends the term', async () => {
    const typist = user()
    renderDirectory()
    await screen.findByText('Nyandeng Lado')

    await typist.type(screen.getByLabelText(/name/i), 'banda')

    // Typing alone does not query.
    expect(lastRegistrarsQuery?.name).toBeUndefined()

    await typist.click(screen.getByRole('button', { name: /search/i }))

    await waitFor(() => expect(lastRegistrarsQuery?.name).toBe('banda'))
  })

  it('shows a distinct empty state when a search matches nobody', async () => {
    renderDirectory()
    await screen.findByText('Nyandeng Lado')

    respondWith(ok(page([])))
    const typist = user()
    await typist.type(screen.getByLabelText(/name/i), 'nobody')
    await typist.click(screen.getByRole('button', { name: /search/i }))

    expect(await screen.findByText(/no registrars match/i)).toBeInTheDocument()
  })

  it('shows the unprovisioned empty state when nobody is listed and nothing is filtered', async () => {
    respondWith(ok(page([])))
    renderDirectory()

    expect(await screen.findByText(/no registrars provisioned/i)).toBeInTheDocument()
  })

  it('offers a retry when the directory cannot be loaded', async () => {
    respondWith(fail(503, { status: 503, title: 'Something went wrong', errors: [] }))
    renderDirectory()

    expect(await screen.findByRole('button', { name: /try again/i })).toBeInTheDocument()
  })
})
