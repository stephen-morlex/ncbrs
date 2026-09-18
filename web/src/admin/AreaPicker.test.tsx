import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AreaPicker } from './AreaPicker'

const get = vi.fn()

// One stable client object, as the real useApiClient memoises — a fresh object
// per render re-runs the mount effect and reloads the states under the person
// mid-selection.
const client = { GET: get }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

/** The API wraps its payload in a { meta, data } envelope; the picker reads data.data. */
function ok<T>(data: T) {
  return { data: { data }, error: undefined, response: { ok: true, status: 200 } }
}

const centralEquatoria = {
  administrativeAreaId: 'area-state-ce',
  name: 'Central Equatoria',
  level: 'State' as const,
  code: 'SS-CE',
  parentId: 'area-country',
}

const juba = {
  administrativeAreaId: 'area-county-juba',
  name: 'Juba',
  level: 'County' as const,
  code: 'SS-CE-JUB',
  parentId: 'area-state-ce',
}

const user = () => userEvent.setup({ delay: null })

beforeEach(() => {
  get.mockReset()
  // Route by what the picker asks for: states at the top, a parent's children
  // below it, and nothing under a county (so the tree stops there).
  get.mockImplementation((_path: string, opts?: { params?: { query?: { level?: string; parentId?: string } } }) => {
    const query = opts?.params?.query ?? {}
    if (query.level === 'State') return Promise.resolve(ok([centralEquatoria]))
    if (query.parentId === centralEquatoria.administrativeAreaId) return Promise.resolve(ok([juba]))
    return Promise.resolve(ok([]))
  })
})

describe('AreaPicker', () => {
  it('starts from the states, not the country root', async () => {
    render(<AreaPicker />)

    await waitFor(() => expect(get).toHaveBeenCalled())
    expect(get).toHaveBeenCalledWith(
      '/api/administrative-areas',
      expect.objectContaining({ params: { query: { level: 'State' } } }),
    )

    // The one dropdown so far is the states.
    await screen.findByLabelText(/state/i)
  })

  it('drills into a selection, fetching its children by parent', async () => {
    const typist = user()
    render(<AreaPicker />)

    await screen.findByLabelText(/state/i)
    await typist.click(screen.getByLabelText(/state/i))
    await typist.click(await screen.findByRole('option', { name: 'Central Equatoria' }))

    // Selecting the state fetches its children by parent, and a county
    // dropdown appears because there are children to choose.
    await waitFor(() =>
      expect(get).toHaveBeenCalledWith(
        '/api/administrative-areas',
        expect.objectContaining({ params: { query: { parentId: centralEquatoria.administrativeAreaId } } }),
      ),
    )
    await screen.findByLabelText(/county/i)
  })

  it('emits the deepest area chosen', async () => {
    const onSelect = vi.fn()
    const typist = user()
    render(<AreaPicker onSelect={onSelect} />)

    await screen.findByLabelText(/state/i)
    await typist.click(screen.getByLabelText(/state/i))
    await typist.click(await screen.findByRole('option', { name: 'Central Equatoria' }))

    expect(onSelect).toHaveBeenCalledWith(centralEquatoria)

    await typist.click(await screen.findByLabelText(/county/i))
    await typist.click(await screen.findByRole('option', { name: 'Juba' }))

    expect(onSelect).toHaveBeenLastCalledWith(juba)
  })

  it('stops adding dropdowns where a branch has no children', async () => {
    const typist = user()
    render(<AreaPicker />)

    await screen.findByLabelText(/state/i)
    await typist.click(screen.getByLabelText(/state/i))
    await typist.click(await screen.findByRole('option', { name: 'Central Equatoria' }))
    await typist.click(await screen.findByLabelText(/county/i))
    await typist.click(await screen.findByRole('option', { name: 'Juba' }))

    // Juba has no children in this fixture, so no third dropdown appears.
    await waitFor(() =>
      expect(get).toHaveBeenCalledWith(
        '/api/administrative-areas',
        expect.objectContaining({ params: { query: { parentId: juba.administrativeAreaId } } }),
      ),
    )
    const comboboxes = screen.getAllByRole('combobox')
    expect(comboboxes).toHaveLength(2)
  })
})
