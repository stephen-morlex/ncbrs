import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Facilities } from './Facilities'

const get = vi.fn()
const post = vi.fn()

const client = { GET: get, POST: post }

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
  roles.value = ['district-officer']
  get.mockResolvedValue(ok(page([facility])))
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
    expect(screen.getByText(/200,000.*200,999/)).toBeInTheDocument()
    expect(post.mock.calls[0][0]).toBe('/api/BirthRecords/{facilityId}/request-brn-block')
    expect(post.mock.calls[0][1].body.data.blockSize).toBe(1000)

    // Done reloads the list so the new numbers-left is reflected.
    get.mockClear()
    await typist.click(screen.getByRole('button', { name: /done/i }))
    await waitFor(() => expect(get).toHaveBeenCalled())
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
