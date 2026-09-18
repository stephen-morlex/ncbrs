import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { VerifyCertificate } from './VerifyCertificate'

const post = vi.fn()

const client = { POST: post }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

function ok<T>(data: T) {
  return { data: { data }, error: undefined, response: { ok: true, status: 200 } }
}

function fail(status: number, body: unknown) {
  return { data: undefined, error: body, response: { ok: false, status } }
}

const user = () => userEvent.setup({ delay: null })

function renderScreen() {
  render(
    <MemoryRouter initialEntries={['/certificates/verify']}>
      <VerifyCertificate />
    </MemoryRouter>,
  )
}

async function verify(payload: string) {
  const typist = user()
  await typist.type(screen.getByLabelText(/scanned qr payload/i), payload)
  await typist.click(screen.getByRole('button', { name: /^verify$/i }))
}

beforeEach(() => {
  post.mockReset()
})

describe('VerifyCertificate', () => {
  it('does not verify an empty payload', async () => {
    renderScreen()
    expect(screen.getByRole('button', { name: /^verify$/i })).toBeDisabled()
  })

  it('shows a valid certificate with its facts read from the payload', async () => {
    post.mockResolvedValue(
      ok({
        valid: true,
        revoked: false,
        brn: '100001',
        childFullName: 'Ayen Deng',
        dateOfBirth: '2026-06-01',
        sex: 'Female',
        issueDateUtc: '2026-09-15T10:00:00Z',
      }),
    )

    renderScreen()
    await verify('NCBRS.v1.payload.SIGNED')

    expect(await screen.findByText(/valid certificate/i)).toBeInTheDocument()
    expect(screen.getByText('Ayen Deng')).toBeInTheDocument()
    expect(screen.getByText('100001')).toBeInTheDocument()
    expect(post.mock.calls[0][1].body.data.qrPayload).toBe('NCBRS.v1.payload.SIGNED')
  })

  it('reads a revoked certificate as revoked, whatever its signature', async () => {
    // A genuinely-signed document whose register entry was later corrected
    // must still read as no longer valid.
    post.mockResolvedValue(
      ok({
        valid: false,
        revoked: true,
        revocationReason: 'RegistrationAnnulled',
        revokedAtUtc: '2026-09-16T10:00:00Z',
        brn: '100001',
        childFullName: 'Ayen Deng',
      }),
    )

    renderScreen()
    await verify('NCBRS.v1.payload.SIGNED')

    expect(await screen.findByText(/this certificate has been revoked/i)).toBeInTheDocument()
    expect(screen.getByText(/registration was annulled/i)).toBeInTheDocument()
    // The facts are still shown so the counter can match the document.
    expect(screen.getByText('Ayen Deng')).toBeInTheDocument()
  })

  it('reads a bad signature as unverifiable', async () => {
    post.mockResolvedValue(
      ok({
        valid: false,
        revoked: false,
        reason: 'The signature did not check out.',
      }),
    )

    renderScreen()
    await verify('tampered')

    expect(await screen.findByText(/could not be verified/i)).toBeInTheDocument()
    expect(screen.getByText(/signature did not check out/i)).toBeInTheDocument()
  })

  it('surfaces a malformed payload the server rejects', async () => {
    post.mockResolvedValue(
      fail(400, { status: 400, title: 'The request was not accepted', errors: [{ field: 'qrPayload', message: 'The payload could not be parsed.' }] }),
    )

    renderScreen()
    await verify('not-a-payload')

    expect(await screen.findByText(/could not be parsed/i)).toBeInTheDocument()
  })
})
