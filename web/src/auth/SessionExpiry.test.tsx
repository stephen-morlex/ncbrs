import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { SessionExpiry } from '@/auth/SessionExpiry'

// The provider's event hub, captured so a test can fire the same callbacks
// oidc-client-ts would. Each `add*` returns its unsubscribe, which the
// component is expected to call on unmount.
const handlers = vi.hoisted(() => ({
  expiring: [] as (() => void)[],
  expired: [] as (() => void)[],
  renewError: [] as (() => void)[],
  userLoaded: [] as (() => void)[],
  unsubscribed: 0,
}))

const signinSilent = vi.hoisted(() => vi.fn())

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({
    signinSilent,
    events: {
      addAccessTokenExpiring: (cb: () => void) => {
        handlers.expiring.push(cb)
        return () => handlers.unsubscribed++
      },
      addAccessTokenExpired: (cb: () => void) => {
        handlers.expired.push(cb)
        return () => handlers.unsubscribed++
      },
      addSilentRenewError: (cb: () => void) => {
        handlers.renewError.push(cb)
        return () => handlers.unsubscribed++
      },
      addUserLoaded: (cb: () => void) => {
        handlers.userLoaded.push(cb)
        return () => handlers.unsubscribed++
      },
    },
  }),
}))

function fire(which: keyof Omit<typeof handlers, 'unsubscribed'>) {
  for (const cb of handlers[which]) {
    cb()
  }
}

describe('SessionExpiry', () => {
  beforeEach(() => {
    handlers.expiring = []
    handlers.expired = []
    handlers.renewError = []
    handlers.userLoaded = []
    handlers.unsubscribed = 0
    signinSilent.mockReset()
    signinSilent.mockResolvedValue(undefined)
  })

  it('says nothing while the session is healthy', () => {
    render(<SessionExpiry />)

    expect(screen.queryByRole('status')).not.toBeInTheDocument()
  })

  /**
   * The whole point of the feature: the warning has to arrive while the user
   * is still signed in, because once the token lapses RequireAuth redirects
   * and the page — with any half-typed form on it — is gone.
   */
  it('warns while the session is still valid, before it expires', async () => {
    render(<SessionExpiry />)

    fire('expiring')

    expect(await screen.findByText(/about to expire/i)).toBeInTheDocument()
    expect(screen.getByText(/discards anything not yet submitted/i)).toBeInTheDocument()
  })

  it('clears itself when a renewal lands', async () => {
    render(<SessionExpiry />)

    fire('expiring')
    expect(await screen.findByText(/about to expire/i)).toBeInTheDocument()

    fire('userLoaded')

    await waitFor(() => expect(screen.queryByRole('status')).not.toBeInTheDocument())
  })

  it('escalates the wording once renewal has actually failed', async () => {
    render(<SessionExpiry />)

    fire('expiring')
    fire('renewError')

    expect(await screen.findByText(/session has ended/i)).toBeInTheDocument()
    expect(screen.getByText(/will be lost/i)).toBeInTheDocument()
  })

  it('treats a lapsed token as failed too', async () => {
    render(<SessionExpiry />)

    fire('expired')

    expect(await screen.findByText(/session has ended/i)).toBeInTheDocument()
  })

  /**
   * A later "expiring" must not walk the message back from failed to merely
   * expiring -- the redirect is still coming and the softer wording would
   * understate it.
   */
  it('does not downgrade a failed session back to a warning', async () => {
    render(<SessionExpiry />)

    fire('renewError')
    fire('expiring')

    expect(await screen.findByText(/session has ended/i)).toBeInTheDocument()
    expect(screen.queryByText(/about to expire/i)).not.toBeInTheDocument()
  })

  it('renews in place when asked to stay signed in', async () => {
    render(<SessionExpiry />)
    fire('expiring')
    await screen.findByText(/about to expire/i)

    await userEvent.click(screen.getByRole('button', { name: /stay signed in/i }))

    expect(signinSilent).toHaveBeenCalledTimes(1)
    await waitFor(() => expect(screen.queryByRole('status')).not.toBeInTheDocument())
  })

  it('reports a failed renewal rather than pretending it worked', async () => {
    signinSilent.mockRejectedValue(new Error('network'))
    render(<SessionExpiry />)
    fire('expiring')
    await screen.findByText(/about to expire/i)

    await userEvent.click(screen.getByRole('button', { name: /stay signed in/i }))

    expect(await screen.findByText(/session has ended/i)).toBeInTheDocument()
  })

  it('unsubscribes from every event on unmount', () => {
    const { unmount } = render(<SessionExpiry />)

    unmount()

    expect(handlers.unsubscribed).toBe(4)
  })
})
