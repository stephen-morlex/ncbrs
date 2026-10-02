import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { components } from '@/api/generated/api'
import { PendingAccounts } from './PendingAccounts'

type Facility = components['schemas']['FacilityResponse']

const get = vi.fn()
const post = vi.fn()
const client = { GET: get, POST: post }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

function ok<T>(data: T, status = 200) {
  return { data: { data }, error: undefined, response: { ok: true, status } }
}

const account = {
  pendingAccountId: 'pending-1',
  displayName: 'Achol Garang',
  username: 'achol.garang',
  email: null,
  realmRoles: ['facility-registrar', 'district-officer'],
  countyCode: 'SS0101',
  firstSeenAtUtc: '2026-10-01T08:00:00Z',
  lastSeenAtUtc: '2026-10-01T08:00:00Z',
}

const facility = (id: string, name: string, countyCode: string): Facility => ({
  facilityId: id, name, tier: 'Clinic', countyCode, connectivityProfile: 'Intermittent',
  brnBlockStart: 100000, brnBlockEnd: 199999, brnBlockNextAvailable: 100000, brnRemaining: 100000,
  blockStatus: 'Healthy', brnWarnBelow: 200,
})

const facilities = [facility('juba', 'Rejaf PHCU', 'SS0101'), facility('terekeka', 'Tali PHCU', 'SS0105')]

const user = () => userEvent.setup({ delay: null })

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  get.mockResolvedValue(ok([account]))
})

async function openBind(typist: ReturnType<typeof user>, ministry = false) {
  render(<PendingAccounts facilities={facilities} ministry={ministry} onBound={vi.fn()} />)
  await typist.click(await screen.findByRole('button', { name: /^add$/i }))
}

describe('PendingAccounts', () => {
  it('shows nothing when no account is waiting', async () => {
    get.mockResolvedValue(ok([]))
    const { container } = render(<PendingAccounts facilities={facilities} ministry={false} onBound={vi.fn()} />)

    await waitFor(() => expect(get).toHaveBeenCalled())
    expect(container).toBeEmptyDOMElement()
  })

  // An officer able to create officers could widen their own oversight.
  it('offers an officer only the staff roles the account holds', async () => {
    const typist = user()
    await openBind(typist)

    await typist.click(screen.getByLabelText('Role'))
    expect(screen.getByRole('option', { name: 'Facility registrar' })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: 'District officer' })).not.toBeInTheDocument()
    expect(screen.queryByRole('option', { name: 'Community health worker' })).not.toBeInTheDocument()
  })

  it('offers only facilities in the county the account was placed in', async () => {
    const typist = user()
    await openBind(typist)

    await typist.click(screen.getByLabelText('Facility'))
    expect(screen.getByRole('option', { name: 'Rejaf PHCU' })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: 'Tali PHCU' })).not.toBeInTheDocument()
  })

  it('binds by the registry\'s id for the account, never its Keycloak subject', async () => {
    post.mockResolvedValue(ok({}, 201))
    const typist = user()
    await openBind(typist)

    await typist.click(screen.getByLabelText('Facility'))
    await typist.click(screen.getByRole('option', { name: 'Rejaf PHCU' }))
    await typist.click(screen.getByRole('button', { name: /^add$/i, hidden: false }))

    await waitFor(() => expect(post).toHaveBeenCalled())
    expect(post.mock.calls[0][1].body.data).toEqual({
      pendingAccountId: 'pending-1',
      facilityId: 'juba',
      role: 'FacilityRegistrar',
      displayName: 'Achol Garang',
    })
  })
})
