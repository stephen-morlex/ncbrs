import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { RevocationList } from './RevocationList'

const get = vi.fn()

const client = { GET: get }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

function ok<T>(data: T) {
  return { data: { data }, error: undefined, response: { ok: true, status: 200 } }
}

function fail(status: number, body: unknown) {
  return { data: undefined, error: body, response: { ok: false, status } }
}

const list = {
  issuer: 'NCBRS',
  version: '1',
  keyId: 'key-2026',
  coversFromUtc: null,
  issuedAtUtc: '2026-09-18T00:00:00Z',
  nextUpdateUtc: '2026-09-19T00:00:00Z',
  count: 1,
  entries: [
    { serialHash: 'a1b2c3d4e5f6a1b2c3d4e5f6', reason: 'RegistrationAnnulled', revokedAtUtc: '2026-09-16T00:00:00Z' },
  ],
  signature: 'SIGNED',
}

function renderScreen() {
  render(
    <MemoryRouter initialEntries={['/certificates/revocations']}>
      <RevocationList />
    </MemoryRouter>,
  )
}

beforeEach(() => {
  get.mockReset()
  get.mockResolvedValue(ok(list))
})

describe('RevocationList', () => {
  it('shows the list metadata and its withdrawn entries', async () => {
    renderScreen()

    // The nextUpdate note is load-bearing, not decoration.
    expect(await screen.findByText(/absent from a cached copy is unknown/i)).toBeInTheDocument()
    expect(screen.getByText('key-2026')).toBeInTheDocument()
    // The opaque digest, and its reason in words.
    expect(screen.getByText('a1b2c3d4e5f6a1b2c3d4e5f6')).toBeInTheDocument()
    expect(screen.getByText('Annulled')).toBeInTheDocument()
  })

  it('shows an empty-but-signed list distinctly, not as an error', async () => {
    // An empty list is a real published fact: a verifier can tell it from a
    // missing one.
    get.mockResolvedValue(ok({ ...list, count: 0, entries: [] }))
    renderScreen()

    expect(await screen.findByText(/no certificates have been withdrawn/i)).toBeInTheDocument()
  })

  it('offers a retry when the list cannot be loaded', async () => {
    get.mockResolvedValue(fail(503, { status: 503, title: 'Something went wrong', errors: [] }))
    renderScreen()

    expect(await screen.findByRole('button', { name: /try again/i })).toBeInTheDocument()
  })
})
