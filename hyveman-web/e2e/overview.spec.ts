/**
 * Browser workflow tests (FRONTEND.md §14): overview → host detail navigation,
 * event search URL persistence, and alert actions, all against the mock API
 * with a CDP virtual WebAuthn authenticator.
 */
import { expect, test, type Page } from '@playwright/test';
import { installVirtualAuthenticator } from './webauthn';

const MOCK = 'http://127.0.0.1:5099';

/** Enters the app through the first-run setup ceremony. */
async function signIn(page: Page, context: import('@playwright/test').BrowserContext) {
  await installVirtualAuthenticator(page, context);
  await page.goto('/'); // setup-required -> /setup
  await expect(page).toHaveURL(/\/setup$/);
  await page.getByTestId('setup-button').click();
  await expect(page).toHaveURL('/');
}

test.beforeEach(async ({ request }) => {
  await request.post(`${MOCK}/__mock/reset`);
});

test('overview to host detail navigation', async ({ page, context }) => {
  await signIn(page, context);

  await expect(page.getByRole('heading', { name: 'dc01' })).toBeVisible();
  await expect(page.getByText('Agent silent', { exact: false }).first()).toBeVisible();

  await page.getByRole('button', { name: 'Open dc01 details' }).click();
  await expect(page).toHaveURL(/\/hosts\/hst_1$/);
  await expect(page.getByRole('tab', { name: 'Components' })).toBeVisible();

  // Component table loads from the health endpoint.
  await page.getByRole('tab', { name: 'Components' }).click();
  await expect(page.getByText('DIMM A1')).toBeVisible();

  // History tab renders charts from the bounded server-bucketed endpoint.
  await page.getByRole('tab', { name: 'Health history' }).click();
  await expect(page.getByText('Rollup state (server-bucketed)')).toBeVisible();

  // VMs tab.
  await page.getByRole('tab', { name: 'VMs' }).click();
  await expect(page.getByText('sql01')).toBeVisible();
});

test('event search persists filters in the URL and opens a detail panel', async ({ page, context }) => {
  await signIn(page, context);

  await page.goto('/logs');
  await page.getByLabel('Channel').fill('System');
  await expect(page).toHaveURL(/channel=System/);

  await page.getByPlaceholder(/disk or/).fill('shutdown');
  // Debounce commits the free-text filter to the URL.
  await expect(page).toHaveURL(/q=shutdown/, { timeout: 2000 });

  await expect(page.getByText('The previous system shutdown was unexpected.')).toBeVisible();
  await page.getByText('The previous system shutdown was unexpected.').click();
  await expect(page.getByRole('heading', { name: /Event 1/ })).toBeVisible();
  // Structured fields are escaped text; raw payload needs the explicit view.
  await expect(page.getByTestId('event-fields')).toBeVisible();
  await page.getByRole('tab', { name: 'Raw payload' }).click();
  await expect(page.getByTestId('event-raw')).toBeVisible();
});

test('alert acknowledgement records a required reason', async ({ page, context }) => {
  await signIn(page, context);

  await page.goto('/alerts');
  await page.getByLabel(/Acknowledge Agent silent/).click();
  // Reason is required before the confirm button enables.
  const confirm = page.getByRole('button', { name: 'Acknowledge' });
  await expect(confirm).toBeDisabled();
  await page.getByLabel(/Reason/).fill('on-call rotation');
  await expect(confirm).toBeEnabled();
  await confirm.click();
  await expect(page.getByText('Agent silent: web01', { exact: true })).toBeVisible();
});
