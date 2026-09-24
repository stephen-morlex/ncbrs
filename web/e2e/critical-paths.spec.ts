import { expect, test } from '@playwright/test'
import { pageAs, registerBirth, uniqueChildName } from './helpers'

/**
 * The four critical paths the web plan names: register, amend and approve,
 * issue, annul — through the real UI, API and identity provider.
 *
 * Each test registers its own birth, so a failure points at one path rather
 * than at whichever test happened to run before it. And each asserts the
 * legal rule the path exists to enforce, not only that a page appeared: a
 * correction that is still pending must not have changed the record; an
 * annulled record must refuse a certificate.
 */

test('a registrar registers a birth and it is on the register', async ({ browser }) => {
  const page = await pageAs(browser, 'registrar')
  const name = uniqueChildName()

  const brn = await registerBirth(page, name)

  expect(brn).toMatch(/^\d+$/)
  await expect(page.getByText(name, { exact: true })).toBeVisible()
})

test('an identity correction waits for a second registrar, then takes effect', async ({ browser }) => {
  const registrar = await pageAs(browser, 'registrar')
  const original = uniqueChildName()
  const corrected = uniqueChildName()
  const brn = await registerBirth(registrar, original)

  // The registrar asks for the child's name to be corrected.
  await registrar.goto(`/records/correct?brn=${brn}`)
  await registrar.getByLabel('Child’s full name').fill(corrected)
  await registrar.getByLabel('Why is this being corrected?').fill('Name misspelled on the original form.')
  await registrar.getByRole('button', { name: 'Submit the correction' }).click()

  await expect(registrar.getByRole('heading', { name: 'Correction submitted' })).toBeVisible()
  await expect(registrar.getByText(/1 change is waiting for a/)).toBeVisible()

  // Pending means pending: the register still says what it said.
  await registrar.goto(`/records?brn=${brn}`)
  await expect(registrar.getByText(original, { exact: true })).toBeVisible()

  // A district officer — not the person who asked — approves it.
  const officer = await pageAs(browser, 'districtOfficer')
  await officer.goto('/review/amendments')
  await officer.getByRole('row', { name: new RegExp(brn) }).getByRole('button', { name: 'Review' }).click()
  await officer.getByPlaceholder('What was checked, and the decision made.').fill('Checked against the clinic register.')
  await officer.getByRole('button', { name: 'Approve' }).click()
  await expect(officer.getByText('Correction approved')).toBeVisible()

  // Now, and only now, the record carries the corrected name.
  await registrar.goto(`/records?brn=${brn}`)
  await expect(registrar.getByText(corrected, { exact: true })).toBeVisible()
  await expect(registrar.getByText(original, { exact: true })).toHaveCount(0)
})

test('a registrar issues a certificate for a registration', async ({ browser }) => {
  const page = await pageAs(browser, 'registrar')
  const brn = await registerBirth(page, uniqueChildName())

  await page.goto(`/records/certificate?brn=${brn}`)
  await expect(page.getByText('No certificate issued yet')).toBeVisible()

  await page.getByRole('button', { name: 'Issue certificate' }).click()

  await expect(page.getByText('Certificate issued')).toBeVisible()
})

test('the Ministry annuls a registration, and it can no longer carry a certificate', async ({ browser }) => {
  const registrar = await pageAs(browser, 'registrar')
  const brn = await registerBirth(registrar, uniqueChildName())

  const ministry = await pageAs(browser, 'ministryAdmin')
  await ministry.goto('/review/annulments')
  await ministry.getByLabel('Registration number').fill(brn)
  await ministry.getByRole('button', { name: 'Find' }).click()

  await ministry.getByLabel('Justification').fill('Registered in error: the same child was registered at another facility the day before.')
  await ministry.getByRole('button', { name: 'Annul this registration' }).click()
  await ministry.getByRole('button', { name: 'Annul the registration' }).click()

  await expect(ministry.getByText('This registration has been annulled')).toBeVisible()

  // Nothing is deleted, and every acting path refuses: the number still
  // resolves, to an explanation, and no certificate can be issued against it.
  await registrar.goto(`/records/certificate?brn=${brn}`)
  await expect(registrar.getByText('No certificate for an annulled registration')).toBeVisible()
  await expect(registrar.getByRole('button', { name: 'Issue certificate' })).toHaveCount(0)
})
