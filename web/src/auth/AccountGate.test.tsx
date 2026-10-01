import { render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import '@/i18n'
import { AccountGate } from './AccountGate'

const get = vi.fn()
const client = { GET: get }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({ user: { access_token: 'token', profile: { name: 'Achol Garang' } } }),
}))

function me(body: Record<string, unknown>) {
  return { data: { data: body }, response: { ok: true, status: 200 } }
}

function renderGate() {
  render(
    <AccountGate>
      <p>the registry</p>
    </AccountGate>,
  )
}

beforeEach(() => {
  get.mockReset()
})

describe('AccountGate', () => {
  it('lets a provisioned account through', async () => {
    get.mockResolvedValue(me({ provisioned: true, withdrawn: false, pending: false, registrar: null }))
    renderGate()

    expect(await screen.findByText('the registry')).toBeInTheDocument()
  })

  it('tells a waiting account it is waiting, by name, instead of a 403 on every screen', async () => {
    get.mockResolvedValue(me({ provisioned: false, withdrawn: false, pending: true, registrar: null }))
    renderGate()

    expect(await screen.findByText(/waiting to be added/i)).toBeInTheDocument()
    expect(screen.getByText(/Achol Garang/)).toBeInTheDocument()
    expect(screen.queryByText('the registry')).not.toBeInTheDocument()
  })

  it('tells a withdrawn account so', async () => {
    get.mockResolvedValue(me({ provisioned: false, withdrawn: true, pending: false, registrar: null }))
    renderGate()

    expect(await screen.findByText(/has been withdrawn/i)).toBeInTheDocument()
    expect(screen.queryByText('the registry')).not.toBeInTheDocument()
  })

  // Blocking the whole site on this one call would turn a blip into an outage.
  it('shows the pages anyway when the registry cannot be asked', async () => {
    get.mockRejectedValue(new Error('offline'))
    renderGate()

    expect(await screen.findByText('the registry')).toBeInTheDocument()
  })
})
