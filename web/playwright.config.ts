import { defineConfig, devices } from '@playwright/test'

/**
 * End-to-end tests over the critical paths (web plan, Phase 7): register,
 * amend and approve, issue, annul — through the real UI, the real API and the
 * real identity provider.
 *
 * The origin is fixed, not configurable: the Keycloak realm's redirect URIs
 * and the API's CORS policy both allow exactly http://localhost:5173, and a
 * suite that drifted to another port would fail at sign-in for a reason that
 * has nothing to do with the code under test.
 *
 * Keycloak must be running (`docker compose up -d keycloak`); the API and the
 * web dev server are started here if they are not already.
 */
const WebOrigin = 'http://localhost:5173'
const ApiOrigin = 'http://localhost:5259'
const ci = !!process.env.CI

export default defineConfig({
  testDir: './e2e',
  // One worker: the flows share one development registry, and a failure in a
  // legal workflow should be read in order, not untangled from interleaving.
  workers: 1,
  fullyParallel: false,
  retries: ci ? 1 : 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: ci ? [['github'], ['html', { open: 'never' }]] : [['list']],
  use: {
    baseURL: WebOrigin,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    // Signs each role in once and keeps the identity provider's session, so
    // the tests themselves never pass through a login form.
    { name: 'setup', testMatch: /auth\.setup\.ts/ },
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
      dependencies: ['setup'],
      testIgnore: /auth\.setup\.ts/,
    },
  ],
  webServer: [
    {
      // Development: migrates and seeds on start (SQLite by default), and the
      // seed's registrars are bound to the realm's test accounts.
      command: 'dotnet run --project ../src/NCBRS.Api --no-launch-profile',
      // Ready means answering at all: /health is behind auth, and Playwright
      // counts a 401 as "up".
      url: `${ApiOrigin}/health`,
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: ApiOrigin },
      reuseExistingServer: !ci,
      timeout: 240_000,
      stdout: 'ignore',
      stderr: 'pipe',
    },
    {
      command: 'npm run dev -- --port 5173 --strictPort',
      url: WebOrigin,
      reuseExistingServer: !ci,
      timeout: 120_000,
    },
  ],
})
