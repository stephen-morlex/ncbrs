/// <reference types="vite/client" />

/**
 * Typed so a missing or misspelled variable is a build error rather than
 * `undefined` reaching Keycloak as an authority and failing at sign-in.
 *
 * Every VITE_* value is compiled into the browser bundle and is therefore
 * public. Nothing secret may be added here.
 */
interface ImportMetaEnv {
  readonly VITE_KEYCLOAK_AUTHORITY: string
  readonly VITE_KEYCLOAK_CLIENT_ID: string
  readonly VITE_API_BASE_URL: string
  readonly VITE_CONSUMER_BASE_URL: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
