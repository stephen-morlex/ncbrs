import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Dhis2Export } from './Dhis2Export'

const get = vi.fn()

const consumer = { GET: get }

vi.mock('@/api/useApi', () => ({
  useConsumerClient: () => consumer,
}))

function okc<T>(data: T) {
  return { data, error: undefined, response: { ok: true, status: 200 } }
}

function fail(status: number, body: unknown) {
  return { data: undefined, error: body, response: { ok: false, status } }
}

const user = () => userEvent.setup({ delay: null })

const exportPayload = {
  dataValueSet: {
    period: '202608',
    dataValues: [
      { dataElement: 'DE_LIVE', period: '202608', orgUnit: 'OU_LUSAKA', value: '120' },
      { dataElement: 'DE_LIVE', period: '202608', orgUnit: 'OU_CENTRAL', value: '80' },
    ],
  },
  suppressed: [{ orgUnit: 'OU_NORTH', reason: 'Below the minimum cell size (5).' }],
  unmapped: ['D-EASTERN-03'],
}

let lastQuery: Record<string, unknown> | undefined

function respond(value: ReturnType<typeof okc> | ReturnType<typeof fail>) {
  lastQuery = undefined
  get.mockImplementation((url: string, opts?: { params?: { query?: Record<string, unknown> } }) => {
    if (url === '/api/exports/dhis2') {
      lastQuery = opts?.params?.query
      return Promise.resolve(value)
    }
    return Promise.resolve(okc(null))
  })
}

function renderScreen() {
  render(
    <MemoryRouter initialEntries={['/exports/dhis2']}>
      <Dhis2Export />
    </MemoryRouter>,
  )
}

beforeEach(() => {
  get.mockReset()
  respond(okc(exportPayload))
})

async function generate() {
  await user().click(screen.getByRole('button', { name: /generate/i }))
}

describe('Dhis2Export', () => {
  it('exports a period as YYYYMM and lists the data values', async () => {
    renderScreen()
    await generate()

    expect(await screen.findByText('OU_LUSAKA')).toBeInTheDocument()
    expect(screen.getByText('120')).toBeInTheDocument()
    // The month input gives YYYY-MM; the query must send YYYYMM.
    expect(String(lastQuery?.period)).toMatch(/^\d{6}$/)
  })

  it('lists withheld districts beside the values, as withheld and not zero', async () => {
    renderScreen()
    await generate()

    expect(await screen.findByText(/withheld — not zero/i)).toBeInTheDocument()
    expect(screen.getByText('OU_NORTH')).toBeInTheDocument()
    expect(screen.getByText(/minimum cell size/i)).toBeInTheDocument()
  })

  it('lists districts with no org-unit mapping rather than dropping them', async () => {
    renderScreen()
    await generate()

    expect(await screen.findByText(/no org-unit mapping/i)).toBeInTheDocument()
    expect(screen.getByText('D-EASTERN-03')).toBeInTheDocument()
  })

  it('explains an empty value set instead of showing a blank table', async () => {
    respond(okc({ dataValueSet: { period: '202608', dataValues: [] }, suppressed: [{ orgUnit: 'OU_NORTH', reason: 'Below the minimum cell size (5).' }], unmapped: [] }))
    renderScreen()
    await generate()

    expect(await screen.findByText(/no values for this period/i)).toBeInTheDocument()
  })

  it('surfaces an invalid period the server rejects', async () => {
    respond(fail(400, { status: 400, title: 'The request was not accepted', errors: [{ field: 'period', message: 'period must be a real calendar month, e.g. 202608.' }] }))
    renderScreen()
    await generate()

    expect(await screen.findByText(/real calendar month/i)).toBeInTheDocument()
  })
})
