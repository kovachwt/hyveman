/**
 * Passkey ceremony helpers (FRONTEND.md §7). The API owns all WebAuthn state;
 * the browser only relays options and the credential response. Responses are
 * never logged or stored. No password/TOTP/backup-code fallbacks exist.
 */
import { startAuthentication, startRegistration } from '@simplewebauthn/browser';
import type {
  AuthenticationResponseJSON,
  PublicKeyCredentialCreationOptionsJSON,
  PublicKeyCredentialRequestOptionsJSON,
  RegistrationResponseJSON,
} from '@simplewebauthn/types';
import { API_BASE, httpFetch } from '@/api/client';

export class PasskeyError extends Error {
  constructor(message: string, readonly causeError?: unknown) {
    super(message);
    this.name = 'PasskeyError';
  }
}

export function passkeysSupported(): { ok: boolean; reason?: string } {
  if (!window.isSecureContext) {
    return { ok: false, reason: 'Passkeys require a secure context (HTTPS or localhost).' };
  }
  if (typeof window.PublicKeyCredential === 'undefined') {
    return { ok: false, reason: 'This browser does not support passkeys (WebAuthn).' };
  }
  return { ok: true };
}

function ceremonyError(err: unknown): PasskeyError {
  if (err instanceof PasskeyError) return err;
  if (err instanceof Error && err.name === 'NotAllowedError') {
    return new PasskeyError('The passkey request was cancelled or denied.', err);
  }
  if (err instanceof Error && err.name === 'SecurityError') {
    return new PasskeyError('The browser blocked the passkey request (insecure context?).', err);
  }
  if (err instanceof Error && err.name === 'NotSupportedError') {
    return new PasskeyError('This browser does not support passkeys (WebAuthn).', err);
  }
  if (err instanceof Error) return new PasskeyError(err.message, err);
  return new PasskeyError('The passkey request failed unexpectedly.', err);
}

export async function beginLogin(): Promise<PublicKeyCredentialRequestOptionsJSON> {
  try {
    return await httpFetch<PublicKeyCredentialRequestOptionsJSON>(
      `${API_BASE}/auth/passkeys/login/options`,
      { method: 'POST' },
    );
  } catch (err) {
    throw ceremonyError(err);
  }
}

export async function completeLogin(
  credential: AuthenticationResponseJSON,
): Promise<void> {
  try {
    await httpFetch<{ ok: boolean }>(`${API_BASE}/auth/passkeys/login/verify`, {
      method: 'POST',
      body: JSON.stringify(credential),
    });
  } catch (err) {
    throw ceremonyError(err);
  }
}

export async function beginRegistration(
  name?: string,
): Promise<PublicKeyCredentialCreationOptionsJSON> {
  try {
    return await httpFetch<PublicKeyCredentialCreationOptionsJSON>(
      `${API_BASE}/auth/passkeys/register/options`,
      {
        method: 'POST',
        body: JSON.stringify({ name: name || null }),
      },
    );
  } catch (err) {
    throw ceremonyError(err);
  }
}

export async function completeRegistration(
  credential: RegistrationResponseJSON,
): Promise<{ id: string }> {
  try {
    return await httpFetch<{ id: string }>(`${API_BASE}/auth/passkeys/register/verify`, {
      method: 'POST',
      body: JSON.stringify(credential),
    });
  } catch (err) {
    throw ceremonyError(err);
  }
}

/** Runs the full browser-side authentication ceremony; the API sets the
 *  session cookie on success. Returns the browser credential response. */
export async function authenticateWithPasskey(): Promise<AuthenticationResponseJSON> {
  const support = passkeysSupported();
  if (!support.ok) throw new PasskeyError(support.reason!);
  const options = await beginLogin();
  try {
    return await startAuthentication({ optionsJSON: options });
  } catch (err) {
    throw ceremonyError(err);
  }
}

/** Runs the full browser-side registration ceremony (first-run setup or an
 *  additional authenticated key). */
export async function registerPasskey(name?: string): Promise<RegistrationResponseJSON> {
  const support = passkeysSupported();
  if (!support.ok) throw new PasskeyError(support.reason!);
  const options = await beginRegistration(name);
  try {
    return await startRegistration({ optionsJSON: options });
  } catch (err) {
    throw ceremonyError(err);
  }
}
