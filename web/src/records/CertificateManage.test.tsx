import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CertificateManage } from './CertificateManage'

const get = vi.fn()
const post = vi.fn()

// One stable client object — the real useApiClient memoises.
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

const baseRecord = {
  birthRecordId: '0199a1b2-0001-7000-8000-000000000001',
  brn: '100001',
  childFullName: 'Chipo Mwale',
  dateOfBirth: '2026-06-01T00:00:00Z',
  sex: 'Female',
  status: 'Confirmed',
  annulment: null,
  lateRegistration: null,
  certificate: null,
}

const issuedCertificate = {
  certificateId: '0199a1b2-cert-7000-8000-000000000001',
  brn: '100001',
  childFullName: 'Chipo Mwale',
  dateOfBirth: '2026-06-01T00:00:00Z',
  sex: 'Female',
  facilityName: 'Lusaka Central Clinic',
  issueDateUtc: '2026-09-15T10:00:00Z',
  qrPayload: 'NCBRS.v1.eyJicm4iOiIxMDAwMDEifQ.SIGNED',
  signature: 'SIGNED',
  reprintCount: 0,
}

// Route GETs by url. The record GET is configurable per test; the certificate
// GET returns the issued certificate when the record says one exists.
function respondRecord(recordValue: ReturnType<typeof ok> | ReturnType<typeof fail>, cert = issuedCertificate) {
  get.mockImplementation((url: string) => {
    if (url === '/api/BirthRecords/{brn}') {
      return Promise.resolve(recordValue)
    }
    if (url === '/api/BirthRecords/{brn}/certificate') {
      return Promise.resolve(ok(cert))
    }
    return Promise.resolve(ok({}))
  })
}

function renderScreen() {
  render(
    <MemoryRouter initialEntries={['/records/certificate?brn=100001']}>
      <CertificateManage />
    </MemoryRouter>,
  )
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()
})

describe('CertificateManage', () => {
  it('offers issuing for a certifiable record with no certificate', async () => {
    respondRecord(ok(baseRecord))
    renderScreen()

    expect(await screen.findByText(/no certificate issued yet/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /issue certificate/i })).toBeEnabled()
  })

  it('issues, then shows the signed certificate', async () => {
    respondRecord(ok(baseRecord))
    post.mockResolvedValue(ok(issuedCertificate, 201))

    const typist = user()
    renderScreen()
    await screen.findByText(/no certificate issued yet/i)

    await typist.click(screen.getByRole('button', { name: /issue certificate/i }))

    expect(await screen.findByText(/certificate issued/i)).toBeInTheDocument()
    // The signed payload is shown so it can be printed and checked.
    expect(screen.getByText(/NCBRS\.v1/)).toBeInTheDocument()
    expect(post.mock.calls[0][0]).toBe('/api/BirthRecords/{brn}/certificate')
  })

  it('shows the refusal reason plainly when a record is not certifiable', async () => {
    // e.g. an unreconciled provisional record — the server knows, the screen
    // does not, so the server's reason is surfaced as-is.
    respondRecord(ok(baseRecord))
    post.mockResolvedValue(
      fail(409, {
        status: 409,
        title: 'Awaiting BRN reconciliation.',
        errors: [{ field: 'brn', message: 'No certificate can be issued until the BRN is reconciled.' }],
      }),
    )

    const typist = user()
    renderScreen()
    await screen.findByText(/no certificate issued yet/i)
    await typist.click(screen.getByRole('button', { name: /issue certificate/i }))

    expect(await screen.findByText(/awaiting brn reconciliation/i)).toBeInTheDocument()
    expect(screen.getByText(/until the brn is reconciled/i)).toBeInTheDocument()
  })

  it('withholds issuing on an unverified late registration, saying why', async () => {
    respondRecord(
      ok({
        ...baseRecord,
        lateRegistration: { daysLate: 200, windowDaysAtFiling: 90, status: 'PendingApproval', evidenceType: 'SchoolRecord' },
      }),
    )
    renderScreen()

    expect(await screen.findByText(/not yet verified/i)).toBeInTheDocument()
  })

  it('reprints an issued certificate, and says the signature and issue date are unchanged', async () => {
    respondRecord(
      ok({ ...baseRecord, certificate: { issuedAtUtc: '2026-09-15T10:00:00Z', withdrawnAtUtc: null, withdrawnReason: null, reprintCount: 0, isValid: true } }),
    )
    post.mockResolvedValue(ok({ ...issuedCertificate, reprintCount: 1 }))

    const typist = user()
    renderScreen()
    await screen.findByText(/certificate issued/i)

    await typist.click(screen.getByRole('button', { name: /^reprint$/i }))

    expect(await screen.findByText(/signature and issue date are unchanged/i)).toBeInTheDocument()
    expect(post.mock.calls[0][0]).toBe('/api/BirthRecords/{brn}/certificate/reprint')
  })

  it('shows a withdrawn certificate as void, with no reissue', async () => {
    respondRecord(
      ok({ ...baseRecord, certificate: { issuedAtUtc: '2026-09-15T10:00:00Z', withdrawnAtUtc: '2026-09-16T10:00:00Z', withdrawnReason: 'Superseded.', reprintCount: 0, isValid: false } }),
    )
    renderScreen()

    expect(await screen.findByText(/this certificate has been withdrawn/i)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /issue certificate/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^reprint$/i })).not.toBeInTheDocument()
  })

  it('refuses a certificate for an annulled record', async () => {
    respondRecord(
      ok({
        ...baseRecord,
        status: 'Annulled',
        annulment: { reason: 'RegisteredInError', justification: 'No such birth.', authorityReference: null, annulledAtUtc: '2026-08-01T00:00:00Z' },
      }),
    )
    renderScreen()

    expect(await screen.findByText(/no certificate for an annulled registration/i)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /issue certificate/i })).not.toBeInTheDocument()
  })
})
