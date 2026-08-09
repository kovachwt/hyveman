/**
 * Browser workflow tests (FRONTEND.md §14): first-run setup and passkey login
 * against the mock API, using a CDP virtual WebAuthn authenticator. Login
 * tests seed the authenticator with a resident key via the setup ceremony.
 */
import { expect, test } from '@playwright/test';
import { installVirtualAuthenticator } from './webauthn';

const MOCK = 'http://127.0.0.1:5099';

test.beforeEach(async ({ request }) => {
  await request.post(`${MOCK}/__mock/reset`);
});

/** Registers the first passkey through the /setup ceremony. */
async function seedPasskey(page: import('@playwright/test').Page) {
  await page.goto('/hosts'); // protected route during setup -> /setup
  await expect(page).toHaveURL(/\/setup$/);
  await page.getByTestId('setup-button').click();
  await expect(page).toHaveURL('/');
}

test.describe('first-run setup', () => {
  test('setup-required visitors are offered /setup; registration enters the app', async ({ page, context }) => {
    await installVirtualAuthenticator(page, context);

    // Protected route during setup bounces to /setup with the trusted-network warning.
    await page.goto('/hosts');
    await expect(page).toHaveURL(/\/setup$/);
    await expect(page.getByTestId('setup-button')).toBeVisible();
    await expect(page.getByText(/trusted setup network/)).toBeVisible();

    // Complete the ceremony against the virtual authenticator.
    await page.getByTestId('setup-button').click();
    await expect(page).toHaveURL('/');
    await expect(page.getByRole('heading', { name: 'dc01' })).toBeVisible();
  });
});

test.describe('passkey login', () => {
  test('unauthenticated visitors are redirected to login and return to the original route', async ({ page, context }) => {
    await installVirtualAuthenticator(page, context);
    await seedPasskey(page);

    // Sign out, then try a protected route: /alerts -> /login -> back to /alerts.
    await page.getByLabel('Admin menu').click();
    await page.getByRole('menuitem', { name: 'Sign out' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Sign out' }).click();
    await expect(page).toHaveURL(/\/login$/);

    await page.goto('/alerts');
    await expect(page).toHaveURL(/\/login$/);

    await page.getByTestId('login-button').click();
    await expect(page).toHaveURL('/alerts');
    await expect(page.getByText('Agent silent: web01')).toBeVisible();
  });

  test('logout returns to the login page and the session is gone', async ({ page, context }) => {
    await installVirtualAuthenticator(page, context);
    await seedPasskey(page);

    await page.getByLabel('Admin menu').click();
    await page.getByRole('menuitem', { name: 'Sign out' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Sign out' }).click();
    await expect(page).toHaveURL(/\/login$/);

    // The protected route bounces again — the session cookie was revoked.
    await page.goto('/');
    await expect(page).toHaveURL(/\/login$/);
  });
});
