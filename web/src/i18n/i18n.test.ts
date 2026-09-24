import { describe, expect, it } from 'vitest'
import i18n, { supportedLanguages } from '@/i18n'
import common from './locales/en/common.json'

/** Every leaf of a resource tree, as [dotted.key, value]. */
function leaves(tree: object, prefix = ''): [string, unknown][] {
  return Object.entries(tree).flatMap(([key, value]) =>
    value !== null && typeof value === 'object'
      ? leaves(value, `${prefix}${key}.`)
      : [[`${prefix}${key}`, value] as [string, unknown]],
  )
}

describe('i18n', () => {
  /**
   * Resources are bundled and initialisation is synchronous, so the very first
   * render already has words. An async start would paint raw keys for a frame
   * -- or for good, if it failed.
   */
  it('is ready before anything renders, and speaks English', () => {
    expect(i18n.isInitialized).toBe(true)
    expect(i18n.language).toBe('en')
    expect(i18n.t('nav.items.devices')).toBe('Devices')
  })

  it('interpolates without escaping twice (React already escapes)', () => {
    expect(i18n.t('app.pageTitle', { page: 'A & B', app: 'NCBRS' })).toBe('A & B · NCBRS')
  })

  it('sets the document language and direction from the active language', () => {
    expect(document.documentElement.lang).toBe('en')
    expect(document.documentElement.dir).toBe('ltr')
  })

  /**
   * The reason direction is derived rather than hard-coded: Arabic is widely
   * spoken in South Sudan, and an Arabic resource must lay the page out right
   * to left without a component changing.
   */
  it('would lay Arabic out right to left', () => {
    expect(i18n.dir('ar')).toBe('rtl')
  })

  it('ships English only, for now', () => {
    expect(supportedLanguages).toEqual(['en'])
  })

  /**
   * An empty string renders as nothing -- a button with no label, a heading
   * that is not there -- and no type check can see it.
   */
  it('has no empty strings in the English resources', () => {
    const empty = leaves(common).filter(([, value]) => typeof value !== 'string' || value.trim() === '')

    expect(empty).toEqual([])
  })
})
