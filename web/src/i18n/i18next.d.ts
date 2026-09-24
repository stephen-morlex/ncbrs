import 'i18next'
import type common from './locales/en/common.json'

/**
 * English is the source of truth for the key set. Every other language is
 * checked against it, and `t()` accepts only keys that exist here — a missing
 * or misspelt key is a type error, not a raw key shown to a registrar.
 */
declare module 'i18next' {
  interface CustomTypeOptions {
    defaultNS: 'common'
    resources: {
      common: typeof common
    }
  }
}
