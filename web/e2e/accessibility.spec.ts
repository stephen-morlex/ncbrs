import AxeBuilder from '@axe-core/playwright'
import { type Page, expect, test } from '@playwright/test'
import { pageAs } from './helpers'
import type { Role } from './users'

/**
 * WCAG 2.2 AA, machine-checkable part, on every signed-in page (plan §17
 * item 10).
 *
 * The item was blocked on "a signed-in browser session"; the end-to-end
 * suite already has one per role, through the real identity provider, so
 * the pages a registrar or an officer actually works in can be scanned as
 * they render for them -- not a signed-out shell.
 *
 * What this covers is what a rule engine can decide: contrast, names and
 * roles, labels, landmarks and headings, ARIA validity, and 2.5.8 target
 * size. What it cannot -- whether a screen-reader user can actually complete
 * a registration, focus order that is valid but confusing, wording -- still
 * needs a person with assistive technology, and this does not claim it.
 *
 * Each page is scanned at desktop width and at phone width: the sidebar
 * collapses to a sheet on a phone, which is a different set of controls, and
 * district offices use both.
 */

const WcagAA = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']

const Widths = [
  { name: 'desktop', width: 1280, height: 800 },
  { name: 'phone', width: 390, height: 844 },
] as const

/**
 * A seeded Juba Teaching Hospital birth: the record pages need a real record
 * to render their content, not the not-found state.
 */
const SeededBrn = '100000'

/** Every page, and a role entitled to see it. */
const Pages: { path: string; role: Role; ready: RegExp | string }[] = [
  { path: '/records', role: 'ministryAdmin', ready: 'Find by number' },
  { path: `/records?brn=${SeededBrn}`, role: 'ministryAdmin', ready: SeededBrn },
  { path: '/records/search', role: 'ministryAdmin', ready: /Search/ },
  { path: '/records/new', role: 'registrar', ready: 'Register the birth' },
  { path: `/records/correct?brn=${SeededBrn}`, role: 'registrar', ready: 'Submit the correction' },
  { path: `/records/certificate?brn=${SeededBrn}`, role: 'registrar', ready: /certificate/i },
  { path: '/review/amendments', role: 'ministryAdmin', ready: /correction|amendment/i },
  { path: '/review/duplicates', role: 'ministryAdmin', ready: /duplicate/i },
  { path: '/review/late-registrations', role: 'ministryAdmin', ready: /late/i },
  { path: '/review/annulments', role: 'ministryAdmin', ready: 'Registration number' },
  { path: '/facilities', role: 'ministryAdmin', ready: /Juba Teaching Hospital/ },
  { path: '/admin/areas', role: 'ministryAdmin', ready: /South Sudan/ },
  { path: '/registrars', role: 'ministryAdmin', ready: /registrar/i },
  { path: '/devices', role: 'ministryAdmin', ready: /TERMINAL-JUBA-01/ },
  { path: '/devices/alerts', role: 'ministryAdmin', ready: /alert/i },
  { path: '/certificates/verify', role: 'ministryAdmin', ready: /verify/i },
  { path: '/certificates/revocations', role: 'ministryAdmin', ready: /revocation/i },
  { path: '/audit', role: 'ministryAdmin', ready: /audit/i },
  { path: '/dashboard', role: 'ministryAdmin', ready: /dashboard/i },
  { path: '/exports/dhis2', role: 'ministryAdmin', ready: /DHIS2/ },
]

/** One line per violation: the rule, its WCAG criteria, and where. */
function describe(violations: Awaited<ReturnType<AxeBuilder['analyze']>>['violations']): string {
  return violations
    .map((violation) => {
      const criteria = violation.tags.filter((tag) => /^wcag\d{3,}$/.test(tag)).join(', ')
      const where = violation.nodes
        .slice(0, 5)
        .map((node) => `      ${node.target.join(' ')}${node.failureSummary ? `\n        ${node.failureSummary.split('\n').slice(1).join('; ')}` : ''}`)
        .join('\n')
      return `  [${violation.impact}] ${violation.id} (${criteria}) — ${violation.help}\n${where}`
    })
    .join('\n')
}

async function settle(page: Page, ready: RegExp | string) {
  // The page's own content, not just the shell: a scan of a loading skeleton
  // passes for the wrong reason.
  await expect(page.getByText(ready).first()).toBeVisible()
  await page.waitForLoadState('networkidle')
}

for (const size of Widths) {
  test.describe(`WCAG 2.2 AA at ${size.name} width`, () => {
    for (const { path, role, ready } of Pages) {
      test(`${path} as ${role}`, async ({ browser }) => {
        const page = await pageAs(browser, role)
        await page.setViewportSize({ width: size.width, height: size.height })
        await page.goto(path)
        await settle(page, ready)

        const results = await new AxeBuilder({ page }).withTags(WcagAA).analyze()

        expect(results.violations, `\n${describe(results.violations)}`).toEqual([])
      })
    }
  })
}
