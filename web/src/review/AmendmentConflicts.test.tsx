import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AmendmentConflicts } from './AmendmentConflicts'

const get = vi.fn()
const post = vi.fn()

// One stable client object, not a fresh one per render — the real useApiClient
// memoises, and a mock that returns a new object each render re-runs the load
// effects and resets the queue.
const client = { GET: get, POST: post }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

function ok<T>(data: T, status = 200) {
  return { data: { data }, error: undefined, response: { ok: true, status } }
}

function fail(status: number, body: unknown) {
  return { data: undefined, error: body, response: { ok: false, status } }
}

const user = () => userEvent.setup({ delay: null })

const conflict = {
  amendmentConflictId: '0199a1b2-c0f0-7000-8000-000000000001',
  amendmentRequestId: '0199a1b2-0001-7000-8000-000000000001',
  brn: '100001',
  childFullName: 'Ayen Deng',
  facilityId: '0199a1b2-fac0-7000-8000-000000000001',
  field: 'BirthWeightGrams',
  expectedPreviousValue: '3200',
  actualPreviousValue: '3350',
  resolvedValue: '3400',
  submittedByRegistrarId: '0199a1b2-reg0-7000-8000-000000000001',
  submittedByRegistrarName: 'Nyandeng Lado',
  detectedAtUtc: '2026-09-10T08:00:00Z',
}

function page(items: unknown[], nextCursor: string | null = null) {
  return { items, total: items.length, nextCursor }
}

function respondWith(conflicts: ReturnType<typeof ok> | ReturnType<typeof fail>) {
  get.mockImplementation((url: string) => {
    if (url === '/api/facilities') {
      return Promise.resolve(ok(page([])))
    }
    if (url === '/api/amendments/conflicts') {
      return Promise.resolve(conflicts)
    }
    return Promise.resolve(ok({}))
  })
}

function renderQueue() {
  render(
    <MemoryRouter initialEntries={['/review/amendments']}>
      <AmendmentConflicts />
    </MemoryRouter>,
  )
}

async function openReview() {
  renderQueue()
  await screen.findByText('100001')
  await user().click(await screen.findByRole('button', { name: /^review$/i }))
  return await screen.findByRole('dialog')
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  respondWith(ok(page([conflict])))
})

describe('AmendmentConflicts', () => {
  it('shows the field and what the device saw against what the register held', async () => {
    renderQueue()

    expect(await screen.findByText('100001')).toBeInTheDocument()
    expect(screen.getByText('BirthWeightGrams')).toBeInTheDocument()
    // The disagreement itself: device's belief vs the register's value, and
    // the value now standing under last-writer-wins.
    expect(screen.getByText('3200')).toBeInTheDocument()
    expect(screen.getByText('3350')).toBeInTheDocument()
    expect(screen.getByText('3400')).toBeInTheDocument()
  })

  it('says a change on the approval track has not been applied, rather than naming a winner', async () => {
    // resolvedValue is null while a change still awaits approval — claiming a
    // standing value there would misdescribe the record.
    respondWith(ok(page([{ ...conflict, resolvedValue: null }])))
    renderQueue()

    expect(await screen.findByText(/not applied — awaiting approval/i)).toBeInTheDocument()
  })

  it('shows an empty queue as the good outcome it is', async () => {
    respondWith(ok(page([])))
    renderQueue()

    expect(await screen.findByText(/no conflicts to review/i)).toBeInTheDocument()
  })

  it('upholds a resolution and clears the row', async () => {
    post.mockResolvedValue(
      ok({
        amendmentConflictId: conflict.amendmentConflictId,
        brn: '100001',
        status: 'Upheld',
        reviewedAtUtc: '2026-09-18T09:00:00Z',
      }),
    )

    const typist = user()
    const dialog = await openReview()
    await typist.type(within(dialog).getByLabelText(/note/i), 'Confirmed against the birth notification.')
    await typist.click(within(dialog).getByRole('button', { name: /uphold/i }))

    expect(await screen.findByText(/resolution upheld/i)).toBeInTheDocument()
    expect(post.mock.calls[0][1].body.data.uphold).toBe(true)

    await typist.click(screen.getByRole('button', { name: /done/i }))
    await waitFor(() => expect(screen.queryByText('100001')).not.toBeInTheDocument())
  })

  it('records a correction made by a separate amendment', async () => {
    post.mockResolvedValue(
      ok({
        amendmentConflictId: conflict.amendmentConflictId,
        brn: '100001',
        status: 'Corrected',
        reviewedAtUtc: '2026-09-18T09:00:00Z',
      }),
    )

    const typist = user()
    const dialog = await openReview()
    await typist.type(within(dialog).getByLabelText(/note/i), 'Restored the notified weight by amendment.')
    await typist.click(within(dialog).getByRole('button', { name: /mark corrected/i }))

    expect(await screen.findByText(/marked as corrected/i)).toBeInTheDocument()
    expect(post.mock.calls[0][1].body.data.uphold).toBe(false)
  })

  it('will not submit a judgement without a reason', async () => {
    const typist = user()
    const dialog = await openReview()

    await typist.click(within(dialog).getByRole('button', { name: /uphold/i }))

    expect(within(dialog).getByText(/a reason is required/i)).toBeInTheDocument()
    expect(post).not.toHaveBeenCalled()
  })

  it('does not offer to change the value from here', async () => {
    // Restoring the previous value is an ordinary amendment, made from the
    // record — one code path for previous values, approval and revocation.
    const dialog = await openReview()

    expect(within(dialog).getByText(/does not change the value/i)).toBeInTheDocument()
    // The submitter is named on the review itself, even though the queue row
    // does not carry a column for it.
    expect(within(dialog).getByText(/Nyandeng Lado/)).toBeInTheDocument()
  })

  it('clears a conflict another registrar already judged', async () => {
    post.mockResolvedValue(
      fail(409, {
        status: 409,
        title: 'Already reviewed.',
        errors: [{ field: 'amendmentConflictId', message: 'This conflict has already been reviewed.' }],
      }),
    )

    const typist = user()
    const dialog = await openReview()
    await typist.type(within(dialog).getByLabelText(/note/i), 'Checked and agreed.')
    await typist.click(within(dialog).getByRole('button', { name: /uphold/i }))

    expect(await screen.findByText(/already reviewed/i)).toBeInTheDocument()

    await typist.click(screen.getByRole('button', { name: /done/i }))
    await waitFor(() => expect(screen.queryByText('100001')).not.toBeInTheDocument())
  })

  it('offers a retry when the queue cannot be loaded', async () => {
    respondWith(fail(503, { status: 503, title: 'Something went wrong', errors: [] }))
    renderQueue()

    expect(await screen.findByRole('button', { name: /try again/i })).toBeInTheDocument()
  })
})
