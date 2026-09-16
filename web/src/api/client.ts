import createClient, { type Middleware } from 'openapi-fetch'
import type { paths as ApiPaths } from '@/api/generated/api'
import type { paths as ConsumerPaths } from '@/api/generated/consumer'

/**
 * The one place the NCBRS wire format is handled.
 *
 * Both services speak the same envelope: a request body is `{ data }` and a
 * response is `{ meta, data }`. That is described accurately in the generated
 * types, because the API's OpenAPI transformer documents the envelope rather
 * than the bare payload -- so unwrapping here is a convenience, not a
 * correction.
 *
 * Two services, two clients, one contract. They are separate because they are
 * separately deployable and separately addressed; a single client with a
 * switch on the path would hide that a dashboard outage is not a
 * registration outage.
 */

export const TransactionIdHeader = 'X-Transaction-Id'
export const ClientIdHeader = 'X-Client-Id'

/** Names this application in the audit trail of every request it makes. */
const ClientId = 'ncbrs-web'

export interface NcbrsClientOptions {
  baseUrl: string

  /**
   * Called for each request. Returns the bearer token, or null when the user
   * is not signed in -- in which case no Authorization header is sent and the
   * server answers 401, which is the honest outcome. Deliberately a callback:
   * the token lives in memory and rotates, so capturing its value here would
   * pin the first one for the life of the client.
   */
  getAccessToken?: () => string | null
}

function ncbrsMiddleware({ getAccessToken }: NcbrsClientOptions): Middleware {
  return {
    async onRequest({ request }) {
      const token = getAccessToken?.()

      if (token) {
        request.headers.set('Authorization', `Bearer ${token}`)
      }

      // The server generates a transaction id when we omit one, and echoes
      // back whichever it used. Sending our own means the id in our logs and
      // the id in the registry's audit trail are the same id -- which is the
      // whole point of the header, and only works if it is set before the
      // request leaves rather than read after it returns.
      if (!request.headers.has(TransactionIdHeader)) {
        request.headers.set(TransactionIdHeader, crypto.randomUUID())
      }

      request.headers.set(ClientIdHeader, ClientId)

      return request
    },
  }
}

export function createApiClient(options: NcbrsClientOptions) {
  const client = createClient<ApiPaths>({ baseUrl: options.baseUrl })
  client.use(ncbrsMiddleware(options))

  return client
}

export function createConsumerClient(options: NcbrsClientOptions) {
  const client = createClient<ConsumerPaths>({ baseUrl: options.baseUrl })
  client.use(ncbrsMiddleware(options))

  return client
}

export type ApiClient = ReturnType<typeof createApiClient>
export type ConsumerClient = ReturnType<typeof createConsumerClient>
