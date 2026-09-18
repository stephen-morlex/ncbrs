import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import type { components } from '@/api/generated/api'
import { RecordDetail } from './RecordDetail'

// The corrections tab fetches on mount. It is not what these tests are about,
// and an unmocked client would make every case depend on a network call.
vi.mock('@/api/useApi', () => ({
  useApiClient: () => ({ GET: vi.fn().mockResolvedValue({ data: { data: [] }, response: { ok: true } }) }),
}))

type BirthRecord = components['schemas']['BirthRecordResponse']

function aRecord(overrides: Partial<BirthRecord> = {}): BirthRecord {
  return {
    birthRecordId: '0199a1b2-0001-7000-8000-000000000001',
    brn: '100001',
    childFullName: 'Ayen Deng',
    dateOfBirth: '2026-06-01T00:00:00Z',
    sex: 'Female',
    status: 'Confirmed',
    ...overrides,
  } as BirthRecord
}

function show(record: BirthRecord) {
  render(
    <MemoryRouter>
      <RecordDetail record={record} />
    </MemoryRouter>,
  )
}

describe('RecordDetail', () => {
  it('warns that no certificate can be issued while late evidence is unverified', () => {
    // The highest-stakes sentence on this screen, and the one the API bug hid:
    // lateRegistration was populated only when registering, never on a lookup,
    // so this could not render for any record a registrar actually opened.
    show(
      aRecord({
        lateRegistration: {
          daysLate: 400,
          windowDaysAtFiling: 90,
          status: 'PendingApproval',
          evidenceType: 'BirthAttendantAttestation',
        },
      }),
    )

    expect(screen.getByText(/No certificate until the evidence is verified/i)).toBeInTheDocument()
    expect(screen.getByText(/Do not tell the family a certificate is available/i)).toBeInTheDocument()
    expect(screen.getByText(/late registration/i)).toBeInTheDocument()
  })

  it('does not warn once the evidence has been verified', () => {
    show(
      aRecord({
        lateRegistration: {
          daysLate: 400,
          windowDaysAtFiling: 90,
          status: 'Approved',
          evidenceType: 'BirthAttendantAttestation',
        },
      }),
    )

    expect(screen.queryByText(/Do not tell the family/i)).not.toBeInTheDocument()
    expect(screen.getByText(/evidence verified/i)).toBeInTheDocument()
  })

  it('never shows a withdrawn certificate as simply issued', () => {
    // A certificate is valid only if signed AND not revoked. Showing the issue
    // date alone would state something false about a legal document to the
    // person being asked about it.
    show(
      aRecord({
        certificate: {
          issuedAtUtc: '2026-06-08T00:00:00Z',
          withdrawnAtUtc: '2026-09-01T00:00:00Z',
          withdrawnReason: 'Superseded by an amendment.',
          reprintCount: 0,
          isValid: false,
        },
      }),
    )

    expect(screen.getByText(/has been withdrawn/i)).toBeInTheDocument()
    expect(screen.getByText(/will fail verification/i)).toBeInTheDocument()
  })

  it('shows a valid certificate with its reprint count', () => {
    show(
      aRecord({
        certificate: {
          issuedAtUtc: '2026-06-08T00:00:00Z',
          withdrawnAtUtc: null,
          withdrawnReason: null,
          reprintCount: 2,
          isValid: true,
        },
      }),
    )

    expect(screen.getByText(/Certificate issued 2026-06-08/i)).toBeInTheDocument()
    expect(screen.getByText(/Reprinted 2 times/i)).toBeInTheDocument()
    expect(screen.queryByText(/withdrawn/i)).not.toBeInTheDocument()
  })

  it('shows the provisional identifier so the slip in a family’s hand matches', () => {
    show(aRecord({ provisionalIdentifier: 'PROV-TABLET07-42' }))

    expect(screen.getByText('PROV-TABLET07-42')).toBeInTheDocument()
    expect(screen.getByText(/Also registered under/i)).toBeInTheDocument()
  })

  it('omits the provisional row entirely when there is not one', () => {
    // Most records were never registered under a provisional number, and a row
    // reading "None" on every screen trains people to stop reading it.
    show(aRecord())

    expect(screen.queryByText(/Also registered under/i)).not.toBeInTheDocument()
  })

  it('keeps explaining an annulled registration rather than falling silent', () => {
    show(
      aRecord({
        status: 'Annulled',
        // Annulment says there was no such birth. A record describing a real
        // child registered twice is a duplicate supersession, which is a
        // different act and keeps one of the records.
        annulment: {
          reason: 'RegisteredInError',
          justification: 'No birth took place; the entry was created in error.',
          authorityReference: 'MIN/2026/14',
          annulledAtUtc: '2026-08-01T00:00:00Z',
        },
      }),
    )

    expect(screen.getByText(/This registration was annulled/i)).toBeInTheDocument()
    expect(screen.getByText(/keeps resolving to this explanation/i)).toBeInTheDocument()
  })
})
