import { defineConfig, devices } from '@playwright/test';

/**
 * Browser tests (FRONTEND.md §14): the Vite dev server proxies /api to the
 * bundled mock API (scripts/mock-api.mjs), which implements the web API
 * surface with no real credentials. Passkey ceremonies are driven with a
 * stubbed navigator.credentials — no production authenticators involved.
 */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  use: {
    // localhost (not 127.0.0.1): the WebAuthn RP ID used by the mock and the
    // real API defaults to "localhost" and must match the page origin.
    baseURL: 'http://localhost:5173',
    trace: 'on-first-retry',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      command: 'node scripts/mock-api.mjs 5099',
      url: 'http://127.0.0.1:5099/__mock/state',
      reuseExistingServer: !process.env.CI,
      timeout: 30_000,
    },
    {
      command: 'npx vite --port 5173 --strictPort --host localhost',
      url: 'http://localhost:5173/login',
      reuseExistingServer: !process.env.CI,
      timeout: 60_000,
      env: { HYVEMAN_API_PROXY: 'http://127.0.0.1:5099' },
    },
  ],
});
