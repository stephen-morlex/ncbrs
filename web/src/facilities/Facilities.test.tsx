import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Facilities } from './Facilities'

const get = vi.fn()
const post = vi.fn()
const put = vi.fn()

const client = { GET: get, POST: post, PUT: put }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({ user: { access_token: 'token' } }),
}))

// The roles the signed-in user carries, swapped per test to exercise the
// grant affordance being shown or withheld.
const roles = vi.hoisted(() => ({ value: ['district-officer'] as string[] }))

vi.mock('@/auth/claims', () => ({
  realmRoles: () => roles.value,
}))

function ok<T>(data: T) {
  return { data: { data }, error: undefined, response: { ok: true, status: 200 } }
}

function fail(status: number, body: unknown) {
  return { data: undefined, error: body, response: { ok: false, status } }
}

const user = () => userEvent.setup({ delay: null })

const facility = {
  facilityId: '0199a1b2-fac0-7000-8000-000000000001',
  name: 'Juba Central Clinic',
  tier: 'Hospital',
  connectivityProfile: 'AlwaysOn',
  brnRemaining: 300,
  brnWarnBelow: 500,
  blockStatus: 'Low',
}

function page(items: unknown[]) {
  return { items, total: items.length, nextCursor: null }
}

function renderScreen() {
  render(
    <MemoryRouter initialEntries={['/facilities']}>
      <Facilities />
    </MemoryRouter>,
  )
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  put.mockReset()
  roles.value = ['district-officer']
  get.mockResolvedValue(ok(page([facility])))
})

describe('Facilities — adding one', () => {
  // A facility comes with a national range of numbers: the Ministry's act.
  it('offers to add a facility to the Ministry only', async () => {
    roles.value = ['ministry-admin']
    renderScreen()
    expect(await screen.findByRole('button', { name: /add a facility/i })).toBeInTheDocument()
  })

  it('does not offer it to a district officer', async () => {
    renderScreen()
    await screen.findByText('Juba Central Clinic')
    expect(screen.queryByRole('button', { name: /add a facility/i })).not.toBeInTheDocument()
  })
})

describe('Facilities — office codes', () => {
  it('shows a facility’s office code', async () => {
    get.mockResolvedValue(ok(page([{ ...facility, officeCode: 'JTH' }])))
    renderScreen()

    expect(await screen.findByText('JTH')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /give a code/i })).not.toBeInTheDocument()
  })

  /** Given once and then never changed: a facility that has one is shown it, not offered another. */
  it('offers the Ministry to give a code only where there is none', async () => {
    roles.value = ['ministry-admin']
    get.mockResolvedValue(ok(page([facility, { ...facility, facilityId: 'other', name: 'Tali PHCU', officeCode: 'TALI' }])))
    renderScreen()

    await screen.findByText('Tali PHCU')
    expect(screen.getAllByRole('button', { name: /give a code/i })).toHaveLength(1)
  })

  it('does not offer a district officer to give one', async () => {
    renderScreen()

    expect(await screen.findByText(/not yet given/i)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /give a code/i })).not.toBeInTheDocument()
  })

  it('gives a code, in capitals, after saying it cannot be changed', async () => {
    roles.value = ['ministry-admin']
    put.mockResolvedValue(ok({ ...facility, officeCode: 'JCC1' }))

    const typist = user()
    renderScreen()
    await typist.click(await screen.findByRole('button', { name: /give a code/i }))

    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText(/cannot be changed later/i)).toBeInTheDocument()

    await typist.type(within(dialog).getByLabelText(/office code/i), 'jcc1')
    await typist.click(within(dialog).getByRole('button', { name: /give the code/i }))

    await waitFor(() => expect(put).toHaveBeenCalled())
    expect(put.mock.calls[0][0]).toBe('/api/facilities/{facilityId}/office-code')
    expect(put.mock.calls[0][1].body.data.officeCode).toBe('JCC1')
  })

  it('will not send a code that is not two to six letters or digits', async () => {
    roles.value = ['ministry-admin']

    const typist = user()
    renderScreen()
    await typist.click(await screen.findByRole('button', { name: /give a code/i }))

    const dialog = await screen.findByRole('dialog')
    await typist.type(within(dialog).getByLabelText(/office code/i), 'J')

    expect(within(dialog).getByRole('button', { name: /give the code/i })).toBeDisabled()
  })
})

describe('Facilities — granting a block', () => {
  it('offers a grant to someone permitted to register births', async () => {
    renderScreen()

    expect(await screen.findByText('Juba Central Clinic')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /grant a block/i })).toBeInTheDocument()
  })

  it('does not offer a grant to someone without that permission', async () => {
    roles.value = []
    renderScreen()

    await screen.findByText('Juba Central Clinic')
    expect(screen.queryByRole('button', { name: /grant a block/i })).not.toBeInTheDocument()
  })

  it('grants a block and shows the allocated range', async () => {
    post.mockResolvedValue(ok({ facilityId: facility.facilityId, blockStart: 200000, blockEnd: 200999 }))

    const typist = user()
    renderScreen()
    await screen.findByText('Juba Central Clinic')

    await typist.click(screen.getByRole('button', { name: /grant a block/i }))

    const dialog = await screen.findByRole('dialog')
    await typist.click(within(dialog).getByRole('button', { name: /grant block/i }))

    expect(await screen.findByText(/block granted/i)).toBeInTheDocument()
    expect(screen.getByText('200,000')).toBeInTheDocument()
    expect(screen.getByText('200,999')).toBeInTheDocument()
    expect(post.mock.calls[0][0]).toBe('/api/BirthRecords/{facilityId}/request-brn-block')
    expect(post.mock.calls[0][1].body.data.blockSize).toBe(1000)

    // Done reloads the list so the new numbers-left is reflected.
    get.mockClear()
    await typist.click(screen.getByRole('button', { name: /done/i }))
    await waitFor(() => expect(get).toHaveBeenCalled())
  })

  /** As the registry writes them: composed under the office code, never composed here. */
  it('shows a composed grant as the registry wrote the numbers', async () => {
    post.mockResolvedValue(
      ok({
        facilityId: facility.facilityId,
        blockStart: 1,
        blockEnd: 1000,
        officeCode: 'JTH',
        year: 2026,
        firstBrn: 'SS-JTH-2026-000001-7',
        lastBrn: 'SS-JTH-2026-001000-Q',
      }),
    )

    const typist = user()
    renderScreen()
    await screen.findByText('Juba Central Clinic')

    await typist.click(screen.getByRole('button', { name: /grant a block/i }))
    const dialog = await screen.findByRole('dialog')
    await typist.click(within(dialog).getByRole('button', { name: /grant block/i }))

    expect(await screen.findByText('SS-JTH-2026-000001-7')).toBeInTheDocument()
    expect(screen.getByText('SS-JTH-2026-001000-Q')).toBeInTheDocument()
  })

  it('surfaces an exhausted range rather than silently doing nothing', async () => {
    post.mockResolvedValue(
      fail(409, {
        status: 409,
        title: 'BRN range exhausted.',
        errors: [{ field: 'facilityId', message: 'A new range must be assigned by the central registry.' }],
      }),
    )

    const typist = user()
    renderScreen()
    await screen.findByText('Juba Central Clinic')

    await typist.click(screen.getByRole('button', { name: /grant a block/i }))
    const dialog = await screen.findByRole('dialog')
    await typist.click(within(dialog).getByRole('button', { name: /grant block/i }))

    expect(await screen.findByText(/range exhausted/i)).toBeInTheDocument()
    expect(screen.getByText(/central registry/i)).toBeInTheDocument()
  })
})
