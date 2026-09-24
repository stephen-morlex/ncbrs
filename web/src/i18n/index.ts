import i18n from 'i18next'
import { initReactI18next } from 'react-i18next'
import common from './locales/en/common.json'

/**
 * Localisation for the management site.
 *
 * One language ships today, but the scaffolding is the part that is expensive
 * to add late: every string written without it is a string someone must find
 * and extract later, under deadline, from a system in daily use.
 *
 * **Resources are bundled, not fetched.** A district office on a slow link
 * should not pay a round trip for its own labels before the first screen can
 * render, and a failed fetch would render keys in place of words.
 *
 * **Keys are type-checked** (see `i18next.d.ts`): `t('nav.items.devicez')` fails
 * the build rather than showing a registrar the raw key.
 *
 * **Direction follows the language.** Arabic is widely spoken in South Sudan;
 * `<html dir>` is set from the active language, so adding an Arabic resource
 * lays the page out right to left without touching a component. Layout that
 * assumes left-to-right (`ml-*`, `left-*`) is what would still need checking.
 */
export const defaultNS = 'common'

export const resources = {
  en: { common },
} as const

/** Languages with a translation. Adding one is a resource file plus an entry here. */
export const supportedLanguages = Object.keys(resources) as (keyof typeof resources)[]

void i18n.use(initReactI18next).init({
  resources,
  lng: 'en',
  fallbackLng: 'en',
  supportedLngs: supportedLanguages,
  defaultNS,
  ns: [defaultNS],
  // React escapes what it renders; escaping here as well would double-escape.
  interpolation: { escapeValue: false },
  // Bundled resources, so there is nothing to wait for: initialise before the
  // first render, and a first paint never shows a key instead of a word.
  initAsync: false,
})

function applyDocumentLanguage(language: string) {
  document.documentElement.lang = language
  document.documentElement.dir = i18n.dir(language)
}

i18n.on('languageChanged', applyDocumentLanguage)
applyDocumentLanguage(i18n.language)

export default i18n
