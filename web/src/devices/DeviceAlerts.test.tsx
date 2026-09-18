import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { DeviceAlerts } from './DeviceAlerts'

const get = vi.fn()
const post = vi.fn()

const client = { GET: get, POST: post }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

function ok<T>(data: T) {
  return { data: { data }, error: undefined, response: { ok: true, status: 200 } }
}

function fail(status: number, body: unknown) {
  return { data: undefined, error: body, response: { ok: false, status } }
}

const user = () => userEvent.setup({ delay: null })

const silent = {
  deviceAlertId: '0199a1b2-al00-7000-8000-000000000001',
  deviceId: 'tablet-001',
  facilityId: '0199a1b2-fac0-7000-8000-000000000001',
  districtId: 'lusaka',
  kind: 'Silent',
  status: 'Open',
  raisedAtUtc: '2026-09-14T00:00:00Z',
  lastSeenAtUtc: '2026-09-01T00:00:00Z',
  daysSilentWhenRaised: 13,
  thresholdDays: 7,
  acknowledgedAtUtc: null,
  acknowledgedByRegistrarId: null,
  acknowledgedByRegistrarName: null,
  acknowledgementNote: null,
  resolvedAtUtc: null,
}

const neverReported = {
  ...silent,
  deviceAlertId: '0199a1b2-al00-7000-8000-000000000002',
  deviceId: 'tablet-002',
  kind: 'NeverReported',
  lastSeenAtUtc: null,
  daysSilentWhenRaised: 30,
  thresholdDays: 21,
}

const facility = {
  facilityId: '0199a1b2-fac0-7000-8000-000000000001',
  name: 'Juba Central Clinic',
  tier: 'Hospital',
  connectivityProfile: 'AlwaysOn',
  brnRemaining: 5000,
  brnWarnBelow: 50,
  blockStatus: 'Healthy',
}

function respondAlerts(value: ReturnType<typeof ok> | ReturnType<typeof fail>) {
  get.mockImplementation((url: string) => {
    if (url === '/api/facilities') {
      return Promise.resolve(ok({ items: [facility], total: 1, nextCursor: null }))
    }
    if (url === '/api/devices/alerts') {
      return Promise.resolve(value)
    }
    return Promise.resolve(ok({}))
  })
}

function renderScreen() {
  render(
    <MemoryRouter initialEntries={['/devices/alerts']}>
      <DeviceAlerts />
    </MemoryRouter>,
  )
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  respondAlerts(ok([silent, neverReported]))
})

describe('DeviceAlerts', () => {
  it('distinguishes a silent device from one that never reported', async () => {
    renderScreen()

    expect(await screen.findByText('tablet-001')).toBeInTheDocument()
    expect(screen.getByText('Went silent')).toBeInTheDocument()
    expect(screen.getByText('Never reported')).toBeInTheDocument()
  })

  it('acknowledges an alert and reflects it, making clear it is not resolved', async () => {
    post.mockResolvedValue(
      ok({
        ...silent,
        status: 'Acknowledged',
        acknowledgedAtUtc: '2026-09-18T00:00:00Z',
        acknowledgedByRegistrarName: 'Nyandeng Lado',
        acknowledgementNote: 'Driving out Thursday.',
      }),
    )

    const typist = user()
    renderScreen()
    await screen.findByText('tablet-001')

    const row = screen.getByText('tablet-001').closest('tr') as HTMLElement
    await typist.click(within(row).getByRole('button', { name: /acknowledge/i }))

    const dialog = await screen.findByRole('dialog')
    // The distinction is stated at the point of action.
    expect(within(dialog).getByText(/only the device reporting again/i)).toBeInTheDocument()
    await typist.type(within(dialog).getByLabelText(/note/i), 'Driving out Thursday.')
    await typist.click(within(dialog).getByRole('button', { name: /^acknowledge$/i }))

    await waitFor(() => expect(screen.getByText(/acknowledged/i)).toBeInTheDocument())
    expect(post.mock.calls[0][0]).toBe('/api/devices/alerts/{deviceAlertId}/acknowledge')
  })

  it('surfaces the conflict when a device reported before the acknowledgement landed', async () => {
    post.mockResolvedValue(
      fail(409, {
        status: 409,
        title: 'Alert already resolved.',
        errors: [{ field: 'deviceAlertId', message: 'The device reported again; there is nothing to act on.' }],
      }),
    )

    const typist = user()
    renderScreen()
    await screen.findByText('tablet-001')

    const row = screen.getByText('tablet-001').closest('tr') as HTMLElement
    await typist.click(within(row).getByRole('button', { name: /acknowledge/i }))

    const dialog = await screen.findByRole('dialog')
    await typist.click(within(dialog).getByRole('button', { name: /^acknowledge$/i }))

    expect(await screen.findByText(/nothing to act on/i)).toBeInTheDocument()
  })

  it('shows the good empty state when nothing is quiet', async () => {
    respondAlerts(ok([]))
    renderScreen()

    expect(await screen.findByText(/no open alerts/i)).toBeInTheDocument()
  })

  it('offers a retry when the queue cannot be loaded', async () => {
    respondAlerts(fail(503, { status: 503, title: 'Something went wrong', errors: [] }))
    renderScreen()

    expect(await screen.findByRole('button', { name: /try again/i })).toBeInTheDocument()
  })
})
