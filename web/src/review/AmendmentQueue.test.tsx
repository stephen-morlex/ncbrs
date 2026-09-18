import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AmendmentQueue } from './AmendmentQueue'

const get = vi.fn()
const post = vi.fn()

// One stable client object, not a fresh one per render. The real useApiClient
// memoises for exactly this reason; a mock that returns a new object each time
// re-runs the load effects on every render, which reset the queue under the
// person reading it. This trap has already bitten two suites in this repo.
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

// userEvent's realistic typing delays buy nothing here and can push a
// sentence-long note past the default timeout.
const user = () => userEvent.setup({ delay: null })

const row = {
  amendmentRequestId: '0199a1b2-0001-7000-8000-000000000001',
  brn: '100001',
  childFullName: 'Ayen Deng',
  facilityId: '0199a1b2-fac0-7000-8000-000000000001',
  facilityName: 'Juba Central Clinic',
  reason: 'Family name omitted at intake.',
  submittedByRegistrarId: '0199a1b2-reg0-7000-8000-000000000001',
  submittedByRegistrarName: 'Nyandeng Lado',
  submittedAtUtc: '2026-09-10T08:00:00Z',
  changes: [
    { field: 'ChildFullName', previousValue: 'Ayen Deng', newValue: 'Ayen Deng Lado' },
  ],
}

function page(items: unknown[], nextCursor: string | null = null) {
  return { items, total: items.length, nextCursor }
}

function respondWith(pending: ReturnType<typeof ok> | ReturnType<typeof fail>) {
  get.mockImplementation((url: string) => {
    if (url === '/api/facilities') {
      return Promise.resolve(ok(page([])))
    }
    if (url === '/api/amendments/pending') {
      return Promise.resolve(pending)
    }
    return Promise.resolve(ok({}))
  })
}

function renderQueue() {
  render(
    <MemoryRouter initialEntries={['/review/amendments']}>
      <AmendmentQueue />
    </MemoryRouter>,
  )
}

async function openReview() {
  renderQueue()
  // The BRN is the unambiguous per-row signal: the child's name also appears
  // inside the diff (as the previous value), so it is not unique on the page.
  await screen.findByText('100001')
  await user().click(await screen.findByRole('button', { name: /^review$/i }))
  return await screen.findByRole('dialog')
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  respondWith(ok(page([row])))
})

describe('AmendmentQueue', () => {
  it('lists a pending correction with its diff, submitter and record', async () => {
    renderQueue()

    expect(await screen.findByText('100001')).toBeInTheDocument()
    expect(screen.getByText('Juba Central Clinic')).toBeInTheDocument()
    // The child's name appears both as the record and as the diff's previous
    // value, so it is present more than once.
    expect(screen.getAllByText('Ayen Deng').length).toBeGreaterThan(0)

    // The submitter is named because approval is a separation-of-duties check.
    expect(screen.getByText('Nyandeng Lado')).toBeInTheDocument()

    // The diff, previous → new.
    expect(screen.getByText('ChildFullName')).toBeInTheDocument()
    expect(screen.getByText('Ayen Deng Lado')).toBeInTheDocument()
  })

  it('shows an empty queue as the good outcome it is', async () => {
    respondWith(ok(page([])))
    renderQueue()

    expect(await screen.findByText(/no corrections waiting/i)).toBeInTheDocument()
    expect(screen.getByText(/queue you want to be empty/i)).toBeInTheDocument()
  })

  it('approves a correction and removes it from the queue', async () => {
    post.mockResolvedValue(
      ok({
        amendmentRequestId: row.amendmentRequestId,
        brn: '100001',
        status: 'Applied',
        changes: row.changes,
        certificateInvalidated: false,
      }),
    )

    const typist = user()
    await openReview()
    await typist.click(screen.getByRole('button', { name: /approve/i }))

    expect(await screen.findByText(/correction approved/i)).toBeInTheDocument()

    const [url, options] = post.mock.calls[0]
    expect(url).toBe('/api/amendments/{amendmentRequestId}/review')
    expect(options.body.data.approve).toBe(true)

    await typist.click(screen.getByRole('button', { name: /done/i }))

    await waitFor(() => expect(screen.queryByText('100001')).not.toBeInTheDocument())
  })

  it('says plainly when approval withdraws a certificate', async () => {
    // Approval is the moment the change bites and the certificate is
    // revoked; a reviewer must be told a printed document just stopped
    // verifying.
    post.mockResolvedValue(
      ok({
        amendmentRequestId: row.amendmentRequestId,
        brn: '100001',
        status: 'Applied',
        changes: row.changes,
        certificateInvalidated: true,
      }),
    )

    const typist = user()
    await openReview()
    await typist.click(screen.getByRole('button', { name: /approve/i }))

    expect(await screen.findByText(/withdrawn/i)).toBeInTheDocument()
    expect(screen.getByText(/fail verification/i)).toBeInTheDocument()
  })

  it('will not refuse without a reason, and sends nothing until one is given', async () => {
    const typist = user()
    const dialog = await openReview()

    await typist.click(within(dialog).getByRole('button', { name: /refuse/i }))

    expect(within(dialog).getByText(/a refusal needs a reason/i)).toBeInTheDocument()
    expect(post).not.toHaveBeenCalled()
  })

  it('refuses with a reason and removes the row', async () => {
    post.mockResolvedValue(ok({}))

    const typist = user()
    const dialog = await openReview()

    await typist.type(within(dialog).getByLabelText(/note/i), 'Not supported by the birth notification.')
    await typist.click(within(dialog).getByRole('button', { name: /refuse/i }))

    expect(await screen.findByText(/correction refused/i)).toBeInTheDocument()
    expect(post.mock.calls[0][1].body.data.approve).toBe(false)

    await typist.click(screen.getByRole('button', { name: /done/i }))
    await waitFor(() => expect(screen.queryByText('100001')).not.toBeInTheDocument())
  })

  it('treats "the record changed since submission" as its own outcome, not a plain error', async () => {
    // The correction may still be right — only not against these values — so it
    // stays in the queue rather than being cleared.
    post.mockResolvedValue(
      fail(409, {
        status: 409,
        title: 'Record changed since submission.',
        errors: [
          {
            field: 'amendmentRequestId',
            message: 'The record changed after this correction was submitted (ChildFullName).',
          },
        ],
      }),
    )

    const typist = user()
    const dialog = await openReview()
    await typist.click(within(dialog).getByRole('button', { name: /approve/i }))

    expect(await screen.findByText(/record changed since this was submitted/i)).toBeInTheDocument()
    expect(screen.getByText(/may still be right/i)).toBeInTheDocument()

    // Distinct from a generic failure...
    expect(screen.queryByText(/already reviewed/i)).not.toBeInTheDocument()

    // ...and the row is kept, because nothing was applied.
    await typist.click(screen.getByRole('button', { name: /done/i }))
    await waitFor(() => expect(screen.getByText('100001')).toBeInTheDocument())
  })

  it('clears a row another registrar already reviewed', async () => {
    post.mockResolvedValue(
      fail(409, {
        status: 409,
        title: 'Already reviewed.',
        errors: [{ field: 'amendmentRequestId', message: 'This amendment request has already been reviewed.' }],
      }),
    )

    const typist = user()
    const dialog = await openReview()
    await typist.click(within(dialog).getByRole('button', { name: /approve/i }))

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
