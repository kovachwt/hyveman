/**
 * Virtual WebAuthn authenticator helper (FRONTEND.md §14): uses the Chromium
 * CDP WebAuthn domain so the passkey ceremonies run against a real in-browser
 * WebAuthn implementation with a resident key — no stubbed navigator APIs.
 */
import type { Page, BrowserContext } from '@playwright/test';

export async function installVirtualAuthenticator(page: Page, context: BrowserContext) {
  const cdp = await context.newCDPSession(page);
  await cdp.send('WebAuthn.enable');
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: {
      protocol: 'ctap2',
      transport: 'internal',
      // Non-resident key: the mock echoes the registered credential id in
      // login options, which the virtual authenticator matches directly.
      hasResidentKey: false,
      hasUserVerification: true,
      isUserVerified: true,
      automaticPresenceSimulation: true,
    },
  });
}
