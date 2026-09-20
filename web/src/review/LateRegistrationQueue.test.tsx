import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { LateRegistrationQueue } from './LateRegistrationQueue'

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

function fail(status: number, body: unknown) {
  return { data: undefined, error: body, response: { ok: false, status } }
}

const user = () => userEvent.setup({ delay: null })

const row = {
  lateRegistrationId: '0199a1b2-1a7e-7000-8000-000000000001',
  brn: '100001',
  childFullName: 'Ayen Deng',
  dateOfBirth: '2025-01-05T00:00:00Z',
  daysLate: 240,
  windowDaysAtFiling: 90,
  evidenceType: 'AntenatalOrDeliveryCard',
  evidenceReference: 'ANC-7781',
  declarantName: 'Nyandeng Deng',
  declarantRelationship: 'Mother',
  facilityId: '0199a1b2-fac0-7000-8000-000000000001',
  facilityName: 'Juba Central Clinic',
  submittedByRegistrarId: '0199a1b2-reg0-7000-8000-000000000001',
  submittedByRegistrarName: 'John Wani',
  submittedAtUtc: '2026-09-10T08:00:00Z',
}

function page(items: unknown[], nextCursor: string | null = null) {
  return { items, total: items.length, nextCursor }
}

function respondWith(pending: ReturnType<typeof ok> | ReturnType<typeof fail>) {
  get.mockImplementation((url: string) => {
    if (url === '/api/facilities') {
      return Promise.resolve(ok(page([])))
    }
    if (url === '/api/late-registrations/pending') {
      return Promise.resolve(pending)
    }
    return Promise.resolve(ok({}))
  })
}

function renderQueue() {
  render(
    <MemoryRouter initialEntries={['/review/late-registrations']}>
      <LateRegistrationQueue />
    </MemoryRouter>,
  )
}

async function openReview() {
  renderQueue()
  await screen.findByText('100001')
  await user().click(await screen.findByRole('button', { name: /^verify$/i }))
  return await screen.findByRole('dialog')
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  respondWith(ok(page([row])))
})

describe('LateRegistrationQueue', () => {
  it('shows the lateness, the evidence in plain words, and the declarant', async () => {
    renderQueue()

    expect(await screen.findByText('100001')).toBeInTheDocument()
    expect(screen.getAllByText(/240 days late/i).length).toBeGreaterThan(0)
    // The evidence enum reads as English, not a token.
    expect(screen.getByText('Antenatal or delivery card')).toBeInTheDocument()
    expect(screen.getByText('ANC-7781')).toBeInTheDocument()
    expect(screen.getByText('Nyandeng Deng')).toBeInTheDocument()
  })

  it('shows an empty queue as the good outcome it is', async () => {
    respondWith(ok(page([])))
    renderQueue()

    expect(await screen.findByText(/no late registrations to verify/i)).toBeInTheDocument()
  })

  it('verifies a late registration, and says the certificate can now issue', async () => {
    post.mockResolvedValue(
      ok({
        lateRegistrationId: row.lateRegistrationId,
        brn: '100001',
        status: 'Approved',
        reviewedAtUtc: '2026-09-18T09:00:00Z',
        reviewNote: 'Card seen.',
      }),
    )

    const typist = user()
    const dialog = await openReview()
    await typist.type(within(dialog).getByLabelText(/note/i), 'Antenatal card seen; dates match.')
    await typist.click(within(dialog).getByRole('button', { name: /^verify$/i }))

    expect(await screen.findByText(/^verified$/i)).toBeInTheDocument()
    expect(screen.getByText(/certificate can now be issued/i)).toBeInTheDocument()
    expect(post.mock.calls[0][1].body.data.approve).toBe(true)

    await typist.click(screen.getByRole('button', { name: /done/i }))
    await waitFor(() => expect(screen.queryByText('100001')).not.toBeInTheDocument())
  })

  it('refuses, and says the registration stands but no certificate issues', async () => {
    // Verification withholds the certificate, not the registration — the child
    // still exists in the register.
    post.mockResolvedValue(
      ok({
        lateRegistrationId: row.lateRegistrationId,
        brn: '100001',
        status: 'Rejected',
        reviewedAtUtc: '2026-09-18T09:00:00Z',
        reviewNote: 'Evidence insufficient.',
      }),
    )

    const typist = user()
    const dialog = await openReview()
    await typist.type(within(dialog).getByLabelText(/note/i), 'Card does not name the child.')
    await typist.click(within(dialog).getByRole('button', { name: /refuse/i }))

    expect(await screen.findByText(/verification refused/i)).toBeInTheDocument()
    expect(screen.getByText(/registration stands/i)).toBeInTheDocument()
    expect(post.mock.calls[0][1].body.data.approve).toBe(false)
  })

  it('requires a reason before either decision', async () => {
    // An unexplained approval is no verification at all; a refusal without a
    // reason cannot stop the same claim being refiled.
    const typist = user()
    const dialog = await openReview()

    await typist.click(within(dialog).getByRole('button', { name: /^verify$/i }))

    expect(within(dialog).getByText(/a reason is required/i)).toBeInTheDocument()
    expect(post).not.toHaveBeenCalled()
  })

  it('names the filer on the review, since the verifier may not be them', async () => {
    const dialog = await openReview()
    expect(within(dialog).getByText(/John Wani/)).toBeInTheDocument()
  })

  it('clears a registration another registrar already reviewed', async () => {
    post.mockResolvedValue(
      fail(409, {
        status: 409,
        title: 'Already reviewed.',
        errors: [{ field: 'lateRegistrationId', message: 'This late registration has already been reviewed.' }],
      }),
    )

    const typist = user()
    const dialog = await openReview()
    await typist.type(within(dialog).getByLabelText(/note/i), 'Confirmed.')
    await typist.click(within(dialog).getByRole('button', { name: /^verify$/i }))

    expect(await screen.findByText(/already reviewed/i)).toBeInTheDocument()

    await typist.click(screen.getByRole('button', { name: /done/i }))
    await waitFor(() => expect(screen.queryByText('100001')).not.toBeInTheDocument())
  })

  it('surfaces a self-verification refusal from the server', async () => {
    // The server refuses a verification by the filer (403); the UI cannot
    // cheaply know its own registrar id, so it surfaces the refusal.
    post.mockResolvedValue(
      fail(403, {
        status: 403,
        title: 'Not permitted.',
        errors: [
          {
            field: 'lateRegistrationId',
            message: 'A late registration cannot be verified by the registrar who filed it.',
          },
        ],
      }),
    )

    const typist = user()
    const dialog = await openReview()
    await typist.type(within(dialog).getByLabelText(/note/i), 'Looks fine to me.')
    await typist.click(within(dialog).getByRole('button', { name: /^verify$/i }))

    expect(await screen.findByText(/who filed it/i)).toBeInTheDocument()
    // Still in the queue — nothing was decided. (The BRN shows in both the row
    // and the still-open dialog, so more than one match is expected.)
    expect(screen.getAllByText('100001').length).toBeGreaterThan(0)
  })

  it('offers a retry when the queue cannot be loaded', async () => {
    respondWith(fail(503, { status: 503, title: 'Something went wrong', errors: [] }))
    renderQueue()

    expect(await screen.findByRole('button', { name: /try again/i })).toBeInTheDocument()
  })
})
