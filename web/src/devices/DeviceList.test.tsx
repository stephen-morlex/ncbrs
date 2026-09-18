import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { DeviceList } from './DeviceList'

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

const enrolled = {
  deviceId: 'tablet-001',
  facilityId: '0199a1b2-fac0-7000-8000-000000000001',
  status: 'Enrolled',
  label: 'Maternity ward',
  enrolledAtUtc: '2026-08-01T00:00:00Z',
  lastSeenAtUtc: '2026-09-15T00:00:00Z',
  statusChangedAtUtc: null,
  statusReason: null,
}

const neverReported = {
  deviceId: 'tablet-002',
  facilityId: '0199a1b2-fac0-7000-8000-000000000001',
  status: 'Enrolled',
  label: null,
  enrolledAtUtc: '2026-08-20T00:00:00Z',
  lastSeenAtUtc: null,
  statusChangedAtUtc: null,
  statusReason: null,
}

const facility = {
  facilityId: '0199a1b2-fac0-7000-8000-000000000001',
  name: 'Lusaka Central Clinic',
  tier: 'Hospital',
  connectivityProfile: 'AlwaysOn',
  brnRemaining: 5000,
  brnWarnBelow: 50,
  blockStatus: 'Healthy',
}

function respondDevices(value: ReturnType<typeof ok> | ReturnType<typeof fail>) {
  get.mockImplementation((url: string) => {
    if (url === '/api/facilities') {
      return Promise.resolve(ok({ items: [facility], total: 1, nextCursor: null }))
    }
    if (url === '/api/devices') {
      return Promise.resolve(value)
    }
    return Promise.resolve(ok({}))
  })
}

function renderScreen() {
  render(
    <MemoryRouter initialEntries={['/devices']}>
      <DeviceList />
    </MemoryRouter>,
  )
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  respondDevices(ok([enrolled, neverReported]))
})

describe('DeviceList', () => {
  it('lists devices with facility name, status, and last-reported', async () => {
    renderScreen()

    expect(await screen.findByText('tablet-001')).toBeInTheDocument()
    expect(screen.getAllByText('Lusaka Central Clinic').length).toBeGreaterThan(0)
    // Both fixtures are enrolled, so "in service" appears on each.
    expect(screen.getAllByText('in service').length).toBe(2)
  })

  it('calls out a device that has never reported', async () => {
    renderScreen()

    expect(await screen.findByText('tablet-002')).toBeInTheDocument()
    expect(screen.getByText(/never reported/i)).toBeInTheDocument()
  })

  it('suspends a device with a reason and reflects the new status', async () => {
    post.mockResolvedValue(ok({ ...enrolled, status: 'Suspended', statusReason: 'Mislaid.' }))

    const typist = user()
    renderScreen()
    await screen.findByText('tablet-001')

    // Act on the first device's row.
    const row = screen.getByText('tablet-001').closest('tr') as HTMLElement
    await typist.click(within(row).getByRole('button', { name: /suspend/i }))

    const dialog = await screen.findByRole('dialog')
    await typist.type(within(dialog).getByLabelText(/reason/i), 'Mislaid at handover.')
    await typist.click(within(dialog).getByRole('button', { name: /^suspend$/i }))

    await waitFor(() => expect(screen.getByText('suspended')).toBeInTheDocument())
    expect(post.mock.calls[0][0]).toBe('/api/devices/{deviceId}/suspend')
    expect(post.mock.calls[0][1].body.data.reason).toBe('Mislaid at handover.')
  })

  it('will not submit a status change without a reason', async () => {
    const typist = user()
    renderScreen()
    await screen.findByText('tablet-001')

    const row = screen.getByText('tablet-001').closest('tr') as HTMLElement
    await typist.click(within(row).getByRole('button', { name: /revoke/i }))

    const dialog = await screen.findByRole('dialog')
    await typist.click(within(dialog).getByRole('button', { name: /^revoke$/i }))

    expect(within(dialog).getByText(/a reason is required/i)).toBeInTheDocument()
    expect(post).not.toHaveBeenCalled()
  })

  it('warns that revocation cannot be undone', async () => {
    const typist = user()
    renderScreen()
    await screen.findByText('tablet-001')

    const row = screen.getByText('tablet-001').closest('tr') as HTMLElement
    await typist.click(within(row).getByRole('button', { name: /revoke/i }))

    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText(/no un-revoke/i)).toBeInTheDocument()
  })

  it('surfaces an invalid-transition conflict from the server', async () => {
    post.mockResolvedValue(
      fail(409, {
        status: 409,
        title: 'Cannot change status',
        errors: [{ field: 'deviceId', message: 'A revoked device cannot be reinstated.' }],
      }),
    )

    const typist = user()
    renderScreen()
    await screen.findByText('tablet-001')

    const row = screen.getByText('tablet-001').closest('tr') as HTMLElement
    await typist.click(within(row).getByRole('button', { name: /suspend/i }))

    const dialog = await screen.findByRole('dialog')
    await typist.type(within(dialog).getByLabelText(/reason/i), 'Trying.')
    await typist.click(within(dialog).getByRole('button', { name: /^suspend$/i }))

    expect(await screen.findByText(/cannot be reinstated/i)).toBeInTheDocument()
  })

  it('offers a retry when the list cannot be loaded', async () => {
    respondDevices(fail(503, { status: 503, title: 'Something went wrong', errors: [] }))
    renderScreen()

    expect(await screen.findByRole('button', { name: /try again/i })).toBeInTheDocument()
  })
})
