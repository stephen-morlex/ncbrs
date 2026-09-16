import { useMemo } from 'react'
import { useAuth } from 'react-oidc-context'
import { createApiClient, createConsumerClient } from '@/api/client'

/**
 * The API clients, wired to the live session.
 *
 * The token is read through a callback at request time rather than captured:
 * it lives in memory, rotates on silent renew, and a client that closed over
 * the first one would keep presenting it until it expired and then start
 * failing for no visible reason.
 *
 * Memoised on the auth object rather than the token, so the client identity
 * is stable across renders and does not retrigger effects that depend on it.
 */
export function useApiClient() {
  const auth = useAuth()

  return useMemo(
    () =>
      createApiClient({
        baseUrl: import.meta.env.VITE_API_BASE_URL,
        getAccessToken: () => auth.user?.access_token ?? null,
      }),
    [auth],
  )
}

export function useConsumerClient() {
  const auth = useAuth()

  return useMemo(
    () =>
      createConsumerClient({
        baseUrl: import.meta.env.VITE_CONSUMER_BASE_URL,
        getAccessToken: () => auth.user?.access_token ?? null,
      }),
    [auth],
  )
}
