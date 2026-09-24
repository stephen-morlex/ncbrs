import { expect, test as setup } from '@playwright/test'
import { type Role, Users, storageFor } from './users'

const realm = 'http://localhost:8080/realms/ncbrs'

setup.beforeAll(async ({ request }) => {
  // Fail with the fix, not with a timeout on a login page that never loads.
  const response = await request.get(realm).catch(() => null)
  if (!response?.ok()) {
    throw new Error(`Keycloak is not answering at ${realm}. Start it with: docker compose up -d keycloak`)
  }
})

for (const role of Object.keys(Users) as Role[]) {
  setup(`sign in as ${Users[role].username}`, async ({ page }) => {
    // Any guarded page redirects to the identity provider.
    await page.goto('/records')

    await page.locator('#username').fill(Users[role].username)
    await page.locator('#password').fill(Users[role].password)
    await page.locator('#kc-login').click()

    // Back in the app, shell rendered: signed in.
    await expect(page).toHaveURL(/localhost:5173\/records/)
    await expect(page.getByRole('link', { name: /skip to main content/i })).toBeAttached()

    // Tokens are held in memory only (see src/auth/oidc.ts), so there is no
    // token to save — what is kept is the identity provider's session cookie,
    // which lets each test sign straight back in without the form.
    await page.context().storageState({ path: storageFor(role) })
  })
}
