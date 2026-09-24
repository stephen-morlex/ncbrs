import { type Browser, type Page, expect } from '@playwright/test'
import { type Role, storageFor } from './users'

/** A fresh page signed in as the given role. */
export async function pageAs(browser: Browser, role: Role): Promise<Page> {
  const context = await browser.newContext({ storageState: storageFor(role) })
  return context.newPage()
}

/**
 * A child's name no other run will have produced. Random letters rather than
 * a counter or timestamp: the duplicate matcher compares names by edit
 * distance, and names that differ by one digit would cluster into duplicate
 * candidates across runs.
 */
export function uniqueChildName(): string {
  const letters = Array.from({ length: 7 }, () => String.fromCharCode(97 + Math.floor(Math.random() * 26))).join('')
  return `Ayen ${letters[0].toUpperCase()}${letters.slice(1)}`
}

/** A date of birth well inside the statutory window, so no late-registration evidence is asked for. */
function recentBirthDate(): string {
  const date = new Date()
  date.setDate(date.getDate() - 3)
  return date.toISOString().slice(0, 10)
}

async function choose(page: Page, label: RegExp | string, option: RegExp | string) {
  await page.getByLabel(label, { exact: typeof label === 'string' }).click()
  await page.getByRole('option', { name: option }).click()
}

/** Registers a birth through the form, as a registrar would, and returns its BRN. */
export async function registerBirth(page: Page, childName: string): Promise<string> {
  await page.goto('/records/new')

  await choose(page, 'Facility', /Juba Teaching Hospital/)
  await page.getByLabel('Child’s full name').fill(childName)
  await page.getByLabel('Date of birth').fill(recentBirthDate())
  await choose(page, 'Sex', 'Female')
  await choose(page, 'Plurality', /Singleton/)

  await page.getByRole('button', { name: 'Register the birth' }).click()

  // Success lands on the record, named by its new number.
  await page.waitForURL(/\/records\?brn=/)
  const brn = new URL(page.url()).searchParams.get('brn')
  expect(brn, 'the registration should have been given a number').toBeTruthy()
  return brn!
}
