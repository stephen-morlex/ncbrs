import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { RegistrarDirectory } from './RegistrarDirectory'

const get = vi.fn()
const post = vi.fn()

// One stable client object — the real useApiClient memoises, and a fresh mock
// per render re-runs the load effect and resets the list.
const client = { GET: get, POST: post }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({ user: { access_token: 'token' } }),
}))

// The signed-in roles, swapped per test. None by default, so the directory
// tests below exercise reading it, not managing it.
const roles = vi.hoisted(() => ({ value: [] as string[] }))

vi.mock('@/auth/claims', () => ({
  realmRoles: () => roles.value,
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
  post.mockReset()
  roles.value = []
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

describe('RegistrarDirectory — adding and withdrawing', () => {
  const waiting = {
    pendingAccountId: 'pending-1',
    displayName: 'Achol Garang',
    username: 'achol.garang',
    email: 'achol@health.gov.ss',
    realmRoles: ['facility-registrar'],
    countyCode: 'SS0101',
    firstSeenAtUtc: '2026-10-01T08:00:00Z',
    lastSeenAtUtc: '2026-10-01T08:00:00Z',
  }

  function answering(pending: unknown[], registrars: unknown[]) {
    get.mockImplementation((url: string) => {
      if (url === '/api/registrars/pending') return Promise.resolve(ok(pending))
      if (url === '/api/registrars') return Promise.resolve(ok(page(registrars)))
      return Promise.resolve(ok(page([])))
    })
  }

  it('shows an officer the accounts waiting to be added', async () => {
    roles.value = ['district-officer']
    answering([waiting], [registrar])
    render(
      <MemoryRouter>
        <RegistrarDirectory />
      </MemoryRouter>,
    )

    expect(await screen.findByText('Achol Garang')).toBeInTheDocument()
    expect(screen.getByText('achol.garang')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /^add$/i })).toBeInTheDocument()
  })

  it('offers withdrawal to an officer, and marks someone already withdrawn instead', async () => {
    roles.value = ['district-officer']
    answering([], [registrar, { ...registrar, registrarId: 'gone', displayName: 'Deng Ayen', withdrawnAtUtc: '2026-09-30T10:00:00Z' }])
    render(
      <MemoryRouter>
        <RegistrarDirectory />
      </MemoryRouter>,
    )

    expect(await screen.findByRole('button', { name: 'Withdraw Nyandeng Lado' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Withdraw Deng Ayen' })).not.toBeInTheDocument()
    expect(screen.getByText(/^withdrawn [0-9]/)).toBeInTheDocument()
  })

  it('offers a PIN reset only to someone with a PIN, and marks who has none', async () => {
    roles.value = ['district-officer']
    answering([], [
      { ...registrar, hasDevicePin: true },
      { ...registrar, registrarId: 'new', displayName: 'Deng Ayen', hasDevicePin: false },
    ])
    render(
      <MemoryRouter>
        <RegistrarDirectory />
      </MemoryRouter>,
    )

    expect(await screen.findByRole('button', { name: 'Reset the PIN of Nyandeng Lado' })).toBeInTheDocument()
    // Nothing to reset: they cannot unlock a tablet until they set one, and the directory says so.
    expect(screen.queryByRole('button', { name: 'Reset the PIN of Deng Ayen' })).not.toBeInTheDocument()
    expect(screen.getByText('no PIN')).toBeInTheDocument()
  })

  it('resets a PIN after saying what it does, and reloads the directory', async () => {
    roles.value = ['district-officer']
    answering([], [{ ...registrar, hasDevicePin: true }])
    post.mockResolvedValue(ok({ ...registrar, hasDevicePin: false }))
    const clicker = user()
    render(
      <MemoryRouter>
        <RegistrarDirectory />
      </MemoryRouter>,
    )

    await clicker.click(await screen.findByRole('button', { name: 'Reset the PIN of Nyandeng Lado' }))
    // The officer is told they do not choose the new PIN.
    expect(screen.getByText(/You do not choose the new one/)).toBeInTheDocument()

    const loadsBefore = get.mock.calls.filter(([url]) => url === '/api/registrars').length
    await clicker.click(screen.getByRole('button', { name: 'Reset PIN' }))

    await waitFor(() =>
      expect(post).toHaveBeenCalledWith('/api/registrars/{registrarId}/reset-device-pin', {
        params: { path: { registrarId: registrar.registrarId } },
      }),
    )
    await waitFor(() =>
      expect(get.mock.calls.filter(([url]) => url === '/api/registrars').length).toBeGreaterThan(loadsBefore),
    )
  })

  it('offers neither to someone who cannot manage registrars', async () => {
    roles.value = []
    answering([waiting], [registrar])
    render(
      <MemoryRouter>
        <RegistrarDirectory />
      </MemoryRouter>,
    )

    await screen.findByText('Nyandeng Lado')
    expect(screen.queryByText('Achol Garang')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /withdraw/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /reset the pin/i })).not.toBeInTheDocument()
  })
})
