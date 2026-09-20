import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { RecordOutcome } from './RecordOutcome'

const get = vi.fn()
const post = vi.fn()

// One stable client object, as useApiClient memoises — a fresh object per
// render re-runs the load effect.
const client = { GET: get, POST: post }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

function ok<T>(data: T) {
  return { data: { data }, error: undefined, response: { ok: true, status: 200 } }
}

const liveRecord = { brn: '100001', childFullName: 'Ayen Deng', annulment: null }

const user = () => userEvent.setup({ delay: null })

function renderAt(brn = '100001') {
  render(
    <MemoryRouter initialEntries={[`/records/outcome?brn=${brn}`]}>
      <RecordOutcome />
    </MemoryRouter>,
  )
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  get.mockResolvedValue(ok(liveRecord))
  post.mockResolvedValue(ok({ birthRecordId: 'x', brn: '100001', daysAfterBirth: 3 }))
})

describe('RecordOutcome', () => {
  it('records a neonatal death against the record via the real endpoint', async () => {
    const typist = user()
    renderAt()

    await screen.findByLabelText('Date of death')
    fireEvent.change(screen.getByLabelText('Date of death'), { target: { value: '2026-06-05' } })
    await typist.type(screen.getByLabelText(/cause-of-death code/i), 'P21.0')
    await typist.click(screen.getByRole('button', { name: /record neonatal death/i }))

    await waitFor(() =>
      expect(post).toHaveBeenCalledWith(
        '/api/BirthRecords/{brn}/neonatal-outcome',
        expect.objectContaining({
          params: { path: { brn: '100001' } },
          body: {
            data: expect.objectContaining({
              deathDateUtc: '2026-06-05T00:00:00Z',
              icdPmTiming: 'Neonatal',
              icdPmCauseCode: 'P21.0',
              deviceId: 'ncbrs-web',
            }),
          },
        }),
      ),
    )

    expect(await screen.findByText(/neonatal death recorded/i)).toBeInTheDocument()
    expect(screen.getByText(/3 days after the birth/i)).toBeInTheDocument()
  })

  it('switches to a maternal death and calls the maternal endpoint', async () => {
    const typist = user()
    renderAt()

    await typist.click(await screen.findByLabelText('Type of outcome'))
    await typist.click(await screen.findByRole('option', { name: /maternal death/i }))

    fireEvent.change(screen.getByLabelText('Date of death'), { target: { value: '2026-06-10' } })
    await typist.type(screen.getByLabelText(/cause-of-death code/i), 'O72.1')
    await typist.click(screen.getByRole('button', { name: /record maternal death/i }))

    await waitFor(() =>
      expect(post).toHaveBeenCalledWith(
        '/api/BirthRecords/{brn}/maternal-outcome',
        expect.objectContaining({
          body: { data: expect.objectContaining({ icdMmCauseCode: 'O72.1', deviceId: 'ncbrs-web' }) },
        }),
      ),
    )
    expect(await screen.findByText(/maternal death recorded/i)).toBeInTheDocument()
  })

  it('refuses on an annulled record instead of inviting a submission that fails', async () => {
    get.mockResolvedValue(ok({ ...liveRecord, annulment: { reason: 'never happened' } }))
    renderAt()

    expect(await screen.findByText(/this registration is annulled/i)).toBeInTheDocument()
    expect(screen.queryByLabelText('Date of death')).not.toBeInTheDocument()
  })
})
