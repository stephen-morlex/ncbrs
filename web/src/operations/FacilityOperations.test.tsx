import { render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { FacilityOperations } from './FacilityOperations'

const get = vi.fn()
const client = { GET: get }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({ user: { access_token: 'token' } }),
}))

const roles = vi.hoisted(() => ({ value: ['district-officer'] as string[] }))

vi.mock('@/auth/claims', () => ({
  realmRoles: () => roles.value,
}))

const generatedAt = '2026-11-25T12:00:00Z'
const daysAgo = (days: number) => new Date(Date.parse(generatedAt) - days * 86_400_000).toISOString()

const none = { count: 0, oldestAtUtc: null }

const healthy = {
  facilityId: 'fac-1',
  name: 'Juba Teaching Hospital',
  countyCode: 'SS0101',
  tier: 'Hospital',
  connectivityProfile: 'AlwaysOn',
  registrationsInWindow: 120,
  lastSyncAtUtc: daysAgo(0.5),
  activeDevices: 3,
  openDeviceAlerts: 0,
  syncedRecordsInWindow: 120,
  refusedRecordsInWindow: 0,
  provisionalOpen: 0,
  unconfirmedNumbers: 0,
  pendingCorrections: { count: 1, oldestAtUtc: daysAgo(2) },
  pendingLateRegistrations: none,
  pendingDuplicates: none,
  certificatesToIssue: 4,
  certificatesToHandOver: 2,
  oldestUncollectedRegisteredAtUtc: daysAgo(5),
}

const struggling = {
  ...healthy,
  facilityId: 'fac-2',
  name: 'Terekeka Village Health Post',
  connectivityProfile: 'OfflineFirst',
  registrationsInWindow: 9,
  lastSyncAtUtc: daysAgo(25),
  activeDevices: 1,
  openDeviceAlerts: 1,
  syncedRecordsInWindow: 12,
  refusedRecordsInWindow: 3,
  provisionalOpen: 2,
  unconfirmedNumbers: 1,
  pendingCorrections: { count: 2, oldestAtUtc: daysAgo(10) },
  oldestUncollectedRegisteredAtUtc: daysAgo(60),
}

const unrecorded = {
  ...healthy,
  facilityId: 'fac-3',
  name: 'Mangala PHCU',
  syncedRecordsInWindow: null,
  refusedRecordsInWindow: null,
}

function rowOf(name: string) {
  return screen.getByText(name).closest('tr')!
}

function renderPage() {
  return render(
    <MemoryRouter>
      <FacilityOperations />
    </MemoryRouter>,
  )
}

beforeEach(() => {
  get.mockReset()
  roles.value = ['district-officer']
  get.mockResolvedValue({
    data: { data: { generatedAtUtc: generatedAt, windowDays: 30, facilities: [healthy, struggling, unrecorded] } },
    error: undefined,
    response: { ok: true, status: 200 },
  })
})

describe('FacilityOperations', () => {
  it('marks what needs a look, in words as well as colour', async () => {
    renderPage()
    await screen.findByText('Terekeka Village Health Post')

    const bad = rowOf('Terekeka Village Health Post')
    // Quiet past its profile's threshold, births refused, numbers waiting,
    // an old correction, an old uncollected certificate, an alert.
    expect(within(bad).getAllByText('(needs attention)')).toHaveLength(5)
    expect(within(bad).getByText('3 of 12')).toBeInTheDocument()
    expect(within(bad).getByText('2 provisional, 1 unconfirmed')).toBeInTheDocument()
    expect(within(bad).getByText('1 alert')).toBeInTheDocument()

    expect(within(rowOf('Juba Teaching Hospital')).queryByText('(needs attention)')).toBeNull()
  })

  it('shows refusals nobody recorded as unknown, never as zero', async () => {
    renderPage()
    await screen.findByText('Mangala PHCU')

    const row = rowOf('Mangala PHCU')
    expect(within(row).getByTitle('Not recorded for the batches in this period')).toHaveTextContent('—')
    expect(within(row).queryByText(/of 120/)).toBeNull()
  })

  it('links each figure to the list behind it, where the reader may open it', async () => {
    renderPage()
    await screen.findByText('Terekeka Village Health Post')

    const row = rowOf('Terekeka Village Health Post')
    const hrefs = within(row).getAllByRole('link').map((link) => link.getAttribute('href'))
    expect(hrefs).toEqual(['/review/amendments', '/certificates/queue', '/devices/alerts'])
  })

  it('gives facility staff no links to lists they cannot open', async () => {
    roles.value = ['facility-registrar']
    renderPage()
    await screen.findByText('Terekeka Village Health Post')

    const hrefs = within(rowOf('Terekeka Village Health Post')).getAllByRole('link').map((link) => link.getAttribute('href'))
    expect(hrefs).toEqual(['/certificates/queue'])
  })
})
