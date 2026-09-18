import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Dashboard } from './Dashboard'

const get = vi.fn()

// The dashboard reads the consumer, not the API.
const consumer = { GET: get }

vi.mock('@/api/useApi', () => ({
  useConsumerClient: () => consumer,
}))

// The consumer returns bodies unwrapped — no { data } envelope.
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
    districtId: null,
    registrations: {
      liveBirths: 1200,
      fetalDeaths: 15,
      vitalEventTypeUnknown: 0,
      annulled: 2,
      male: 610,
      female: 590,
      sexRatio: 1.03,
    },
    timeliness: { withinWindow: 1000, outsideWindow: 200, unknown: 0, withinWindowShare: 0.83 },
    timeToConfirmation: { confirmed: 1100, stillUnconfirmed: 100, medianDays: 2.5, byFacilityTier: [] },
    registrationDelay: {
      measured: 1100,
      notMeasurable: 100,
      medianDaysBirthToRegistration: 5,
      medianDaysRegistrationToCentre: 1,
      byFacilityTier: [],
    },
    // The point of the whole screen: zero deaths recorded, but the rate cannot
    // be computed — it must read "not available", never 0.
    mortality: {
      neonatalDeaths: 0,
      maternalDeaths: 0,
      neonatalDeathsPerThousandLiveBirths: null,
      maternalDeathsPerHundredThousandLiveBirths: null,
    },
    sync: {
      batches: 40,
      devicesReporting: 12,
      recordsSubmitted: 500,
      recordsRegistered: 480,
      recordsRejected: 20,
      registeredShare: 0.96,
    },
    duplicates: { duplicatesSeen: 3, perTenThousandBirths: null, caveat: 'Only sync-path duplicates are counted.' },
    notAvailable: ['Perinatal mortality rate', 'Registration completeness'],
    ...overrides,
  }
}

// The last summary query the mock answered, so a test can assert the drill-down.
let lastSummaryQuery: Record<string, unknown> | undefined

function respond(summaryValue: ReturnType<typeof okc> | ReturnType<typeof fail>) {
  lastSummaryQuery = undefined
  get.mockImplementation((url: string, opts?: { params?: { query?: Record<string, unknown> } }) => {
    if (url === '/api/dashboard/districts') {
      return Promise.resolve(
        okc([{ districtId: 'lusaka', liveBirths: 100, annulled: 1, withinWindowShare: 0.8 }]),
      )
    }
    if (url === '/api/dashboard/summary') {
      lastSummaryQuery = opts?.params?.query
      return Promise.resolve(summaryValue)
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
  get.mockReset()
  respond(okc(summary()))
})

describe('Dashboard', () => {
  it('renders counts, and a rate whose inputs are unknown as "not available", never zero', async () => {
    renderScreen()

    expect(await screen.findByText('1,200')).toBeInTheDocument()
    // Zero deaths recorded shows as a count...
    expect(screen.getAllByText('0').length).toBeGreaterThan(0)
    // ...but the rates that cannot be computed read "not available".
    expect(screen.getAllByText(/not available/i).length).toBeGreaterThanOrEqual(2)
  })

  it('flags a period that is still filling', async () => {
    respond(okc(summary({ period: { fromUtc: '2026-09-01T00:00:00Z', toUtc: '2026-09-30T00:00:00Z', stillFilling: true } })))
    renderScreen()

    expect(await screen.findByText(/still filling/i)).toBeInTheDocument()
    expect(screen.getByText(/only rise/i)).toBeInTheDocument()
  })

  it('names the indicators it cannot produce rather than showing them as zero', async () => {
    renderScreen()

    expect(await screen.findByText('Perinatal mortality rate')).toBeInTheDocument()
    expect(screen.getByText('Registration completeness')).toBeInTheDocument()
  })

  it('drills down to a district, sending its id', async () => {
    const typist = user()
    renderScreen()
    await screen.findByText('1,200')

    await typist.click(screen.getByRole('combobox'))
    await typist.click(await screen.findByRole('option', { name: 'lusaka' }))

    await waitFor(() => expect(lastSummaryQuery?.districtId).toBe('lusaka'))
  })

  it('offers a retry when the reporting service cannot be reached', async () => {
    respond(fail(503, { status: 503, title: 'Something went wrong', errors: [] }))
    renderScreen()

    expect(await screen.findByRole('button', { name: /try again/i })).toBeInTheDocument()
  })
})
