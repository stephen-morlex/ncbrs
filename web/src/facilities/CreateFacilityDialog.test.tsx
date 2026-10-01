import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CreateFacilityDialog } from './CreateFacilityDialog'

const get = vi.fn()
const post = vi.fn()
const client = { GET: get, POST: post }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

function ok<T>(data: T, status = 200) {
  return { data: { data }, error: undefined, response: { ok: true, status } }
}

const state = { administrativeAreaId: 'area-ce', name: 'Central Equatoria', level: 'State' as const, code: 'SS01', parentId: 'area-ss' }
const juba = { administrativeAreaId: 'area-juba', name: 'Juba', level: 'County' as const, code: 'SS0101', parentId: 'area-ce' }

const created = {
  facilityId: 'f-1', name: 'Rejaf PHCU', tier: 'VillageHealthPost', countyCode: 'SS0101', connectivityProfile: 'OfflineFirst',
  brnBlockStart: 900000, brnBlockEnd: 999999, brnBlockNextAvailable: 900000, brnRemaining: 100000, blockStatus: 'Healthy', brnWarnBelow: 400,
}

const user = () => userEvent.setup({ delay: null })

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  get.mockImplementation((_path: string, opts?: { params?: { query?: { level?: string; parentId?: string } } }) => {
    const query = opts?.params?.query ?? {}
    if (query.level === 'State') return Promise.resolve(ok([state]))
    if (query.parentId === state.administrativeAreaId) return Promise.resolve(ok([juba]))
    return Promise.resolve(ok([]))
  })
})

async function fill(typist: ReturnType<typeof user>) {
  await typist.type(screen.getByLabelText('Name'), 'Rejaf PHCU')
  await typist.click(screen.getByLabelText('Tier'))
  await typist.click(await screen.findByRole('option', { name: 'Village health post' }))
  await typist.click(screen.getByLabelText('Connectivity'))
  await typist.click(await screen.findByRole('option', { name: /Offline first/ }))
  await typist.click(await screen.findByLabelText(/state/i))
  await typist.click(await screen.findByRole('option', { name: 'Central Equatoria' }))
  await typist.click(await screen.findByLabelText(/county/i))
  await typist.click(await screen.findByRole('option', { name: 'Juba' }))
}

describe('CreateFacilityDialog', () => {
  it('sends where the facility is, and never a county or a range of numbers', async () => {
    post.mockResolvedValue(ok(created, 201))
    const onCreated = vi.fn()
    const typist = user()
    render(<CreateFacilityDialog onClose={vi.fn()} onCreated={onCreated} />)

    await fill(typist)
    await typist.click(screen.getByRole('button', { name: 'Add facility' }))

    await waitFor(() => expect(onCreated).toHaveBeenCalledWith(created))
    const body = post.mock.calls[0][1].body.data
    expect(body).toEqual({
      name: 'Rejaf PHCU',
      tier: 'VillageHealthPost',
      connectivityProfile: 'OfflineFirst',
      administrativeAreaId: 'area-juba',
    })
  })

  it('cannot be submitted until it says where the facility is', async () => {
    const typist = user()
    render(<CreateFacilityDialog onClose={vi.fn()} onCreated={vi.fn()} />)

    await typist.type(screen.getByLabelText('Name'), 'Rejaf PHCU')

    expect(screen.getByRole('button', { name: 'Add facility' })).toBeDisabled()
  })

  it('puts the registry\'s refusal beside the field it is about', async () => {
    post.mockResolvedValue({
      data: undefined,
      error: { status: 409, title: 'Name already used in this county.', errors: [{ field: 'data.name', message: 'SS0101 already has a facility called \'Rejaf PHCU\'.' }] },
      response: { ok: false, status: 409 },
    })
    const typist = user()
    render(<CreateFacilityDialog onClose={vi.fn()} onCreated={vi.fn()} />)

    await fill(typist)
    await typist.click(screen.getByRole('button', { name: 'Add facility' }))

    expect(await screen.findByText(/already has a facility called/)).toBeInTheDocument()
    expect(screen.getByLabelText('Name')).toHaveAttribute('aria-invalid', 'true')
  })
})
