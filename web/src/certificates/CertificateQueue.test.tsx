import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CertificateQueue } from './CertificateQueue'

const get = vi.fn()
const post = vi.fn()
const client = { GET: get, POST: post }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({ user: { access_token: 'token' } }),
}))

const roles = vi.hoisted(() => ({ value: ['facility-registrar'] as string[] }))

vi.mock('@/auth/claims', () => ({
  realmRoles: () => roles.value,
}))

function ok<T>(data: T, status = 200) {
  return { data: { data }, error: undefined, response: { ok: true, status } }
}

function fail(status: number, body: unknown) {
  return { data: undefined, error: body, response: { ok: false, status } }
}

const user = () => userEvent.setup({ delay: null })

const toIssue = {
  birthRecordId: 'rec-1',
  brn: 'SS-TVP-2026-000001-K',
  childFullName: 'Ayen Deng',
  dateOfBirth: '2026-09-01T00:00:00Z',
  facilityId: 'fac-1',
  facilityName: 'Terekeka Village Health Post',
  registeredAtUtc: '2026-09-02T08:00:00Z',
  stage: 'ToIssue',
  certificateIssuedAtUtc: null,
}

const toHandOver = {
  ...toIssue,
  birthRecordId: 'rec-2',
  brn: 'SS-TVP-2026-000002-H',
  childFullName: 'Garang Lado',
  stage: 'ToHandOver',
  certificateIssuedAtUtc: '2026-10-01T08:00:00Z',
}

const waiting = {
  ...toIssue,
  birthRecordId: 'rec-3',
  brn: 'PROV-TAB-01-000004',
  childFullName: 'Nyibol Akol',
  stage: 'AwaitingNumber',
}

function queue(items: unknown[]) {
  return ok({ items, total: items.length, nextCursor: null })
}

function renderPage() {
  return render(
    <MemoryRouter>
      <CertificateQueue />
    </MemoryRouter>,
  )
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  roles.value = ['facility-registrar']
  get.mockImplementation((path: string) =>
    path === '/api/certificate-queue' ? Promise.resolve(queue([toIssue, toHandOver, waiting])) : Promise.resolve(ok({ items: [] })),
  )
})

describe('CertificateQueue', () => {
  it('lists each birth with its stage and the next step', async () => {
    renderPage()

    const issueRow = (await screen.findByText('Ayen Deng')).closest('tr')!
    expect(within(issueRow).getByText('To issue and print')).toBeInTheDocument()
    expect(within(issueRow).getByRole('link', { name: 'Issue' })).toHaveAttribute(
      'href',
      `/records/certificate?brn=${encodeURIComponent(toIssue.brn)}`,
    )

    const handOverRow = screen.getByText('Garang Lado').closest('tr')!
    expect(within(handOverRow).getByRole('button', { name: 'Hand over' })).toBeInTheDocument()

    const waitingRow = screen.getByText('Nyibol Akol').closest('tr')!
    expect(within(waitingRow).getByText(/provisional number/)).toBeInTheDocument()
    expect(within(waitingRow).queryByRole('button')).toBeNull()
  })

  it('asks who received it, and takes the birth off the list once recorded', async () => {
    post.mockResolvedValue(ok({}))
    const typist = user()
    renderPage()

    const row = (await screen.findByText('Garang Lado')).closest('tr')!
    await typist.click(within(row).getByRole('button', { name: 'Hand over' }))

    // Never preselected: most often it is the mother, which is why a default would be wrong.
    expect(screen.getByRole('button', { name: 'Record handover' })).toBeDisabled()

    await typist.click(screen.getByLabelText('Who received it'))
    await typist.click(await screen.findByRole('option', { name: 'The father' }))
    await typist.click(screen.getByRole('button', { name: 'Record handover' }))

    await waitFor(() => expect(screen.queryByText('Garang Lado')).toBeNull())
    expect(post).toHaveBeenCalledWith('/api/BirthRecords/{brn}/certificate/handover', {
      params: { path: { brn: toHandOver.brn } },
      body: { data: { deviceId: 'ncbrs-web', receivedBy: 'Father' } },
    })
  })

  it('keeps the birth and says why when the certificate no longer stands', async () => {
    post.mockResolvedValue(
      fail(409, {
        status: 409,
        title: 'No certificate to hand over.',
        errors: [{ field: 'brn', message: 'It has no certificate to hand over. Issue and print it first.' }],
      }),
    )
    const typist = user()
    renderPage()

    const row = (await screen.findByText('Garang Lado')).closest('tr')!
    await typist.click(within(row).getByRole('button', { name: 'Hand over' }))
    await typist.click(screen.getByLabelText('Who received it'))
    await typist.click(await screen.findByRole('option', { name: 'The mother' }))
    await typist.click(screen.getByRole('button', { name: 'Record handover' }))

    expect(await screen.findByText(/Issue and print it first/)).toBeInTheDocument()
    expect(screen.getAllByText('Garang Lado').length).toBeGreaterThan(0)
  })

  it('filters by stage', async () => {
    const typist = user()
    renderPage()
    await screen.findByText('Ayen Deng')

    await typist.click(screen.getByLabelText('Stage'))
    await typist.click(await screen.findByRole('option', { name: 'Printed, to hand over' }))

    await waitFor(() =>
      expect(get).toHaveBeenLastCalledWith('/api/certificate-queue', {
        params: { query: { limit: 25, stage: 'ToHandOver' } },
      }),
    )
  })

  it('offers facility staff no facility filter: they see their own', async () => {
    renderPage()
    await screen.findByText('Ayen Deng')

    expect(screen.queryByLabelText('Facility')).toBeNull()
    expect(get).not.toHaveBeenCalledWith('/api/facilities', expect.anything())
  })

  it('offers an officer the facility filter and names each birth’s facility', async () => {
    roles.value = ['district-officer']
    renderPage()
    await screen.findByText('Ayen Deng')

    expect(screen.getByLabelText('Facility')).toBeInTheDocument()
    expect(screen.getAllByText('Terekeka Village Health Post').length).toBeGreaterThan(0)
  })
})
