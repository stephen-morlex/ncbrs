import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { DuplicateReview } from './DuplicateReview'

const get = vi.fn()
const post = vi.fn()

// One stable client object — the real useApiClient memoises, and a fresh mock
// object per render re-runs the load effects and resets the queue.
const client = { GET: get, POST: post }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

function ok<T>(data: T, status = 200) {
  return { data: { data }, error: undefined, response: { ok: true, status } }
}

// The review endpoint answers 204 with no body.
function noContent() {
  return { data: undefined, error: undefined, response: { ok: true, status: 204 } }
}

function fail(status: number, body: unknown) {
  return { data: undefined, error: body, response: { ok: false, status } }
}

const user = () => userEvent.setup({ delay: null })

const row = {
  duplicateCandidateId: '0199a1b2-d0f0-7000-8000-000000000001',
  score: 87,
  reasons: 'Same mother, same date and place of birth.',
  brn: '100001',
  childFullName: 'Chipo Mwale',
  matchedBrn: '100002',
  matchedChildFullName: 'Chipo Mwale',
  detectedAtUtc: '2026-09-10T08:00:00Z',
}

function page(items: unknown[], nextCursor: string | null = null) {
  return { items, total: items.length, nextCursor }
}

function respondWith(pending: ReturnType<typeof ok> | ReturnType<typeof fail>) {
  get.mockImplementation((url: string) => {
    if (url === '/api/facilities') {
      return Promise.resolve(ok(page([])))
    }
    if (url === '/api/duplicates/pending') {
      return Promise.resolve(pending)
    }
    return Promise.resolve(ok({}))
  })
}

function renderQueue() {
  render(
    <MemoryRouter initialEntries={['/review/duplicates']}>
      <DuplicateReview />
    </MemoryRouter>,
  )
}

async function openReview() {
  renderQueue()
  await screen.findByText('100002')
  await user().click(await screen.findByRole('button', { name: /^review$/i }))
  return await screen.findByRole('dialog')
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  respondWith(ok(page([row])))
})

describe('DuplicateReview', () => {
  it('shows both records, the match score, and why it was flagged', async () => {
    renderQueue()

    // Both BRNs — the matched one is unique on the page.
    expect(await screen.findByText('100002')).toBeInTheDocument()
    expect(screen.getByText('100001')).toBeInTheDocument()
    expect(screen.getByText('87 / 100')).toBeInTheDocument()
    expect(screen.getByText(/same mother, same date and place/i)).toBeInTheDocument()
  })

  it('shows an empty queue plainly', async () => {
    respondWith(ok(page([])))
    renderQueue()

    expect(await screen.findByText(/no suspected duplicates/i)).toBeInTheDocument()
  })

  it('confirms a duplicate and explains the earlier record is kept', async () => {
    post.mockResolvedValue(noContent())

    const typist = user()
    const dialog = await openReview()
    await typist.type(within(dialog).getByLabelText(/note/i), 'Same mother; hospital record repeats the post.')
    await typist.click(within(dialog).getByRole('button', { name: /same birth/i }))

    expect(await screen.findByText(/recorded as one child/i)).toBeInTheDocument()
    expect(screen.getByText(/earlier registration is kept/i)).toBeInTheDocument()
    expect(post.mock.calls[0][1].body.data.isDuplicate).toBe(true)

    await typist.click(screen.getByRole('button', { name: /done/i }))
    await waitFor(() => expect(screen.queryByText('100002')).not.toBeInTheDocument())
  })

  it('dismisses as different births, leaving both standing', async () => {
    post.mockResolvedValue(noContent())

    const typist = user()
    const dialog = await openReview()
    await typist.type(within(dialog).getByLabelText(/note/i), 'Different mothers; coincidental name.')
    await typist.click(within(dialog).getByRole('button', { name: /different births/i }))

    expect(await screen.findByText(/recorded as different births/i)).toBeInTheDocument()
    expect(screen.getByText(/both registrations stand/i)).toBeInTheDocument()
    expect(post.mock.calls[0][1].body.data.isDuplicate).toBe(false)
  })

  it('requires a reason before either decision', async () => {
    const typist = user()
    const dialog = await openReview()

    await typist.click(within(dialog).getByRole('button', { name: /same birth/i }))

    expect(within(dialog).getByText(/a reason is required/i)).toBeInTheDocument()
    expect(post).not.toHaveBeenCalled()
  })

  it('clears a pair another reviewer already decided', async () => {
    post.mockResolvedValue(
      fail(409, {
        status: 409,
        title: 'Already reviewed.',
        errors: [{ field: 'duplicateCandidateId', message: 'This candidate was already confirmed.' }],
      }),
    )

    const typist = user()
    const dialog = await openReview()
    await typist.type(within(dialog).getByLabelText(/note/i), 'Agreed.')
    await typist.click(within(dialog).getByRole('button', { name: /same birth/i }))

    expect(await screen.findByText(/already reviewed/i)).toBeInTheDocument()

    await typist.click(screen.getByRole('button', { name: /done/i }))
    await waitFor(() => expect(screen.queryByText('100002')).not.toBeInTheDocument())
  })

  it('offers a retry when the queue cannot be loaded', async () => {
    respondWith(fail(503, { status: 503, title: 'Something went wrong', errors: [] }))
    renderQueue()

    expect(await screen.findByRole('button', { name: /try again/i })).toBeInTheDocument()
  })
})
