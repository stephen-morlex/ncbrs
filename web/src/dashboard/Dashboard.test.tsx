import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Dashboard } from './Dashboard'

const get = vi.fn()

const consumer = { GET: get }

vi.mock('@/api/useApi', () => ({
  useConsumerClient: () => consumer,
}))

// Recharts measures its container, which jsdom cannot; give it a fixed size so
// the charts mount without warnings. The tests assert on the surrounding
// numbers and states, not on SVG internals.
vi.mock('react-oidc-context', () => ({
  useAuth: () => ({ user: { access_token: 'token' } }),
}))

// The signed-in user's roles, swapped per test: the Ministry picks an area,
// a district officer is shown their own county.
const roles = vi.hoisted(() => ({ value: ['ministry-admin'] as string[] }))

vi.mock('@/auth/claims', () => ({
  realmRoles: () => roles.value,
}))

vi.mock('recharts', async (importActual) => {
  const actual = await importActual<typeof import('recharts')>()
  return {
    ...actual,
    ResponsiveContainer: ({ children }: { children: React.ReactNode }) => (
      <div style={{ width: 800, height: 300 }}>{children}</div>
    ),
  }
})

function okc<T>(data: T) {
  return { data, error: undefined, response: { ok: true, status: 200 } }
}

function fail(status: number, body: unknown) {
  return { data: undefined, error: body, response: { ok: false, status } }
}

const user = () => userEvent.setup({ delay: null })

function summary(overrides: Record<string, unknown> = {}) {
  return {
    period: { fromUtc: '2026-08-01T00:00:00Z', toUtc: '2026-08-31T00:00:00Z', stillFilling: false },
    countyCode: null,
    registrations: { liveBirths: 1200, fetalDeaths: 15, vitalEventTypeUnknown: 0, annulled: 2, male: 610, female: 590, sexRatio: 103.4 },
    timeliness: { withinWindow: 1000, outsideWindow: 200, unknown: 0, withinWindowShare: 83.3 },
    timeToConfirmation: { confirmed: 1100, stillUnconfirmed: 100, medianDays: 2.5, byFacilityTier: [] },
    registrationDelay: { measured: 1100, notMeasurable: 100, medianDaysBirthToRegistration: 5, medianDaysRegistrationToCentre: 1, byFacilityTier: [] },
    mortality: { neonatalDeaths: 0, maternalDeaths: 0, neonatalDeathsPerThousandLiveBirths: null, maternalDeathsPerHundredThousandLiveBirths: null },
    sync: { batches: 40, devicesReporting: 12, recordsSubmitted: 500, recordsRegistered: 480, recordsRejected: 20, registeredShare: 96.0 },
    duplicates: { duplicatesSeen: 3, perTenThousandBirths: null, caveat: 'Only sync-path duplicates are counted.' },
    notAvailable: ['Perinatal mortality rate', 'Registration completeness'],
    ...overrides,
  }
}

function trendPoint(month: number, overrides: Record<string, unknown> = {}) {
  const from = `2026-${String(month).padStart(2, '0')}-01T00:00:00Z`
  const to = `2026-${String(month + 1).padStart(2, '0')}-01T00:00:00Z`
  return {
    period: { fromUtc: from, toUtc: to, stillFilling: false },
    liveBirths: 100,
    fetalDeaths: 2,
    annulled: 0,
    withinWindowShare: 80,
    neonatalDeaths: 1,
    maternalDeaths: 0,
    ...overrides,
  }
}

let lastSummaryQuery: Record<string, unknown> | undefined

function respond(options: {
  summary?: ReturnType<typeof okc> | ReturnType<typeof fail>
  trends?: unknown[]
  districts?: unknown[]
} = {}) {
  lastSummaryQuery = undefined
  get.mockImplementation((url: string, opts?: { params?: { query?: Record<string, unknown> } }) => {
    if (url === '/api/dashboard/summary') {
      lastSummaryQuery = opts?.params?.query
      return Promise.resolve(options.summary ?? okc(summary()))
    }
    if (url === '/api/dashboard/trends') {
      return Promise.resolve(okc(options.trends ?? [trendPoint(7), trendPoint(8, { period: { fromUtc: '2026-08-01T00:00:00Z', toUtc: '2026-09-01T00:00:00Z', stillFilling: true } })]))
    }
    if (url === '/api/dashboard/counties') {
      return Promise.resolve(okc(options.districts ?? [{ countyCode: 'SS0101', liveBirths: 100, annulled: 1, withinWindowShare: 80 }]))
    }
    return Promise.resolve(okc(null))
  })
}

function renderScreen() {
  render(
    <MemoryRouter initialEntries={['/dashboard']}>
      <Dashboard />
    </MemoryRouter>,
  )
}

beforeEach(() => {
  vi.useRealTimers()
  get.mockReset()
  roles.value = ['ministry-admin']
  respond()
})

describe('Dashboard', () => {
  /**
   * The boxes say what the figures cover. "To" includes the day it names,
   * while the service counts births *before* its `to`, so the page sends the
   * day after -- or today's births would be left out of "today".
   */
  it('opens on today, including today', async () => {
    const now = new Date()
    const pad = (value: number) => String(value).padStart(2, '0')
    const today = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`
    const tomorrow = new Date(`${today}T00:00:00Z`)
    tomorrow.setUTCDate(tomorrow.getUTCDate() + 1)

    renderScreen()
    await screen.findByText('1,200')

    expect(screen.getByLabelText('From')).toHaveValue(today)
    expect(screen.getByLabelText('To')).toHaveValue(today)
    expect(lastSummaryQuery?.from).toBe(today)
    expect(lastSummaryQuery?.to).toBe(tomorrow.toISOString().slice(0, 10))
  })

  it('reads the period back as the boxes name it, not by its exclusive end', async () => {
    respond({ summary: okc(summary({ period: { fromUtc: '2026-08-01T00:00:00Z', toUtc: '2026-09-01T00:00:00Z', stillFilling: false } })) })
    renderScreen()

    expect(await screen.findByText(/2026-08-01 to 2026-08-31/)).toBeInTheDocument()
  })

  it('shows KPI figures, and renders a share as a percentage (not multiplied twice)', async () => {
    renderScreen()

    expect(await screen.findByText('1,200')).toBeInTheDocument()
    // withinWindowShare is already a percentage (83.3); it must read 83.3%, not 8330%.
    expect(screen.getByText('83.3%')).toBeInTheDocument()
    expect(screen.getByText('96.0%')).toBeInTheDocument()
  })

  it('renders a rate whose inputs are unknown as "not available", never zero', async () => {
    renderScreen()

    await screen.findByText('1,200')
    // Zero deaths recorded shows as a count, but the rate cannot be computed.
    expect(screen.getAllByText(/not available/i).length).toBeGreaterThanOrEqual(2)
  })

  it('flags a still-filling period', async () => {
    respond({ summary: okc(summary({ period: { fromUtc: '2026-09-01T00:00:00Z', toUtc: '2026-09-30T00:00:00Z', stillFilling: true } })) })
    renderScreen()

    await screen.findByText('1,200')
    expect(screen.getByText('This period is still filling')).toBeInTheDocument()
  })

  it('names the indicators it cannot produce', async () => {
    renderScreen()

    expect(await screen.findByText('Perinatal mortality rate')).toBeInTheDocument()
  })

  /**
   * As the contract names it. This test used to assert `countyCode`, the
   * parameter the page sent -- which the service has never accepted, so the
   * drill-down silently returned national figures while this passed.
   */
  it('drills into a county, sending it as the contract names it', async () => {
    const typist = user()
    renderScreen()
    await screen.findByText('1,200')

    await typist.click(screen.getByRole('combobox'))
    await typist.click(await screen.findByRole('option', { name: 'SS0101' }))

    await waitFor(() => expect(lastSummaryQuery?.districtId).toBe('SS0101'))
    expect(lastSummaryQuery).not.toHaveProperty('countyCode')
  })

  /**
   * A district officer's dashboard is their county: stated, not offered as a
   * choice, and no county is requested -- the service answers with theirs.
   */
  it("shows a district officer their own county, with nothing to pick", async () => {
    roles.value = ['district-officer']
    respond({ summary: okc(summary({ countyCode: 'SS0101' })) })
    renderScreen()

    expect(await screen.findByText('Your county · SS0101')).toBeInTheDocument()
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument()
    expect(lastSummaryQuery?.districtId).toBeUndefined()
  })

  it('can be paused, and refreshes on demand', async () => {
    const typist = user()
    renderScreen()
    await screen.findByText('1,200')

    const live = screen.getByRole('button', { name: /live/i })
    await typist.click(live)
    expect(screen.getByRole('button', { name: /paused/i })).toBeInTheDocument()

    get.mockClear()
    await typist.click(screen.getByRole('button', { name: /refresh/i }))
    await waitFor(() => expect(get).toHaveBeenCalled())
  })

  it('offers a retry when the reporting service cannot be reached', async () => {
    respond({ summary: fail(503, { status: 503, title: 'Something went wrong', errors: [] }) })
    renderScreen()

    expect(await screen.findByRole('button', { name: /try again/i })).toBeInTheDocument()
  })
})
