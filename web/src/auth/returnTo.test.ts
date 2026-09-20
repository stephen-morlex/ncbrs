import { describe, expect, it } from 'vitest'
import { returnTo } from '@/auth/returnTo'

describe('returnTo', () => {
  it('returns the path the user asked for', () => {
    expect(returnTo({ returnTo: '/records/BRN-2026-0001' })).toBe('/records/BRN-2026-0001')
  })

  it('keeps the query string, which carries search and paging state', () => {
    expect(returnTo({ returnTo: '/records?district=SS0101&after=abc' })).toBe(
      '/records?district=SS0101&after=abc',
    )
  })

  describe('refuses anything that would leave the site', () => {
    // Each of these would otherwise turn the callback into an open redirect:
    // a genuine sign-in that lands the user on someone else's page, at the
    // moment they are most primed to type identity data.
    it.each([
      ['an absolute http URL', 'http://evil.example/'],
      ['an absolute https URL', 'https://evil.example/'],
      ['a protocol-relative URL', '//evil.example/'],
      ['a backslash variant browsers normalise', '/\\evil.example/'],
      ['a javascript: URL', 'javascript:alert(1)'],
      ['a relative path with no leading slash', 'records/BRN-1'],
    ])('%s', (_label, candidate) => {
      expect(returnTo({ returnTo: candidate })).toBe('/')
    })
  })

  describe('refuses anything that is not a path at all', () => {
    it.each([
      ['undefined state', undefined],
      ['null state', null],
      ['a bare string', 'just-a-string'],
      ['a number', { returnTo: 42 }],
      ['an object', { returnTo: { href: '/records' } }],
      ['a missing key', { somethingElse: '/records' }],
    ])('%s', (_label, state) => {
      expect(returnTo(state)).toBe('/')
    })
  })

  it('refuses to send the user back to the callback, which has no code left to redeem', () => {
    expect(returnTo({ returnTo: '/auth/callback' })).toBe('/')
    expect(returnTo({ returnTo: '/auth/callback?code=abc' })).toBe('/')
  })
})
