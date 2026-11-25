import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ProblemReports } from './ProblemReports'

const get = vi.fn()
const post = vi.fn()
const client = { GET: get, POST: post }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

function ok<T>(data: T) {
  return { data: { data }, error: undefined, response: { ok: true, status: 200 } }
}

const user = () => userEvent.setup({ delay: null })

const report = {
  reportId: 'rep-1',
  deviceId: 'TAB-0A1B2C3D4E5F',
  facilityId: 'fac-1',
  facilityName: 'Terekeka Village Health Post',
  countyCode: 'SS0104',
  reportedByRegistrarId: 'reg-1',
  reportedByName: 'Nurse Lado',
  writtenAtUtc: '2026-11-22T09:00:00Z',
  receivedAtUtc: '2026-11-25T09:00:00Z',
  description: 'Sync says the sign-in ended.',
  diagnostics: {
    appVersion: '1.0.4',
    platform: 'Android 14',
    language: 'ar',
    signedIn: false,
    staffCount: 2,
    waitingToSync: 7,
    refused: 1,
    uploadInFlight: false,
    drafts: 0,
    numbersLeft: 140,
    provisionalIssued: 0,
    lastSyncedAtUtc: '2026-11-18T09:00:00Z',
    bundleFetchedAtUtc: null,
    clockVerdict: 'Behind',
    clockOffsetSeconds: -86_400,
    failedPinAttempts: 0,
    pinLockouts: 0,
    lastSyncProblems: ['The centre did not accept the sign-in. Sign in again.'],
  },
  resolvedAtUtc: null,
  resolvedByName: null,
  resolutionNote: null,
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  get.mockResolvedValue(ok([report]))
})

describe('ProblemReports', () => {
  it('shows what the registrar wrote and what the tablet said, its faults marked', async () => {
    render(<ProblemReports />)

    expect(await screen.findByText('Sync says the sign-in ended.')).toBeInTheDocument()
    expect(screen.getByText('Behind by 24 hours')).toHaveClass('text-destructive')
    expect(screen.getByText('No — needs signing in again')).toHaveClass('text-destructive')
    expect(screen.getByText('7')).not.toHaveClass('text-destructive')
    expect(screen.getByText('The centre did not accept the sign-in. Sign in again.')).toBeInTheDocument()
  })

  it('resolves a report with a note, and takes it off the open list', async () => {
    post.mockResolvedValue(ok({ ...report, resolvedAtUtc: '2026-11-25T10:00:00Z', resolvedByName: 'Officer Kenyi', resolutionNote: 'Signed in again by phone.' }))
    const typist = user()
    render(<ProblemReports />)

    await typist.click(await screen.findByRole('button', { name: 'Resolve' }))
    await typist.type(screen.getByLabelText('What was done (optional)'), 'Signed in again by phone.')
    await typist.click(screen.getByRole('button', { name: 'Mark resolved' }))

    await waitFor(() => expect(screen.queryByText('Sync says the sign-in ended.')).toBeNull())
    expect(post).toHaveBeenCalledWith('/api/problem-reports/{reportId}/resolve', {
      params: { path: { reportId: 'rep-1' } },
      body: { data: { note: 'Signed in again by phone.' } },
    })
  })

  it('asks for resolved reports too when shown them', async () => {
    const typist = user()
    render(<ProblemReports />)
    await screen.findByText('Sync says the sign-in ended.')

    await typist.click(screen.getByRole('button', { name: 'Show resolved' }))

    await waitFor(() =>
      expect(get).toHaveBeenLastCalledWith('/api/problem-reports', { params: { query: { includeResolved: true } } }),
    )
  })

  it('says when nothing is open', async () => {
    get.mockResolvedValue(ok([]))
    render(<ProblemReports />)

    expect(await screen.findByText('No problems reported')).toBeInTheDocument()
    expect(within(document.body).queryByRole('button', { name: 'Resolve' })).toBeNull()
  })
})
