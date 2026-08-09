import { describe, expect, it } from 'vitest';
import { redirectTarget, safeRedirectPath, sessionStatus } from './guards';
import type { SessionResponse } from '@/api/generated/endpoints';

function session(overrides: Partial<SessionResponse> = {}): SessionResponse {
  return { authenticated: false, setupRequired: false, adminName: null, ...overrides };
}

describe('sessionStatus', () => {
  it('maps the bootstrap states (FRONTEND.md §5)', () => {
    expect(sessionStatus(undefined, true)).toBe('loading');
    expect(sessionStatus(session({ authenticated: true }), false)).toBe('authenticated');
    expect(sessionStatus(session({ setupRequired: true }), false)).toBe('setup');
    expect(sessionStatus(session(), false)).toBe('anonymous');
  });

  it('never treats a stale cached session as authenticated during bootstrap', () => {
    // A previous query result must not render protected routes while the
    // current bootstrap is still loading.
    expect(sessionStatus(session({ authenticated: true }), true)).toBe('loading');
  });
});

describe('redirectTarget', () => {
  it('allows the login page only for anonymous visitors', () => {
    expect(redirectTarget('anonymous', '/login')).toBeNull();
    expect(redirectTarget('authenticated', '/login')).toBe('/');
    expect(redirectTarget('setup', '/login')).toBe('/setup');
    expect(redirectTarget('loading', '/login')).toBeNull();
  });

  it('gates protected routes behind authentication', () => {
    expect(redirectTarget('authenticated', '/hosts')).toBeNull();
    expect(redirectTarget('anonymous', '/hosts')).toBe('/login');
    expect(redirectTarget('setup', '/hosts')).toBe('/setup');
  });

  it('lets setup render only when setup is required', () => {
    expect(redirectTarget('setup', '/setup')).toBeNull();
    expect(redirectTarget('authenticated', '/setup')).toBe('/');
    expect(redirectTarget('anonymous', '/setup')).toBe('/login');
  });
});

describe('safeRedirectPath', () => {
  it('accepts internal paths and rejects external/relative targets', () => {
    expect(safeRedirectPath('/hosts/abc')).toBe('/hosts/abc');
    expect(safeRedirectPath('/')).toBe('/');
    expect(safeRedirectPath('https://evil.example')).toBe('/');
    expect(safeRedirectPath('//evil.example')).toBe('/');
    expect(safeRedirectPath('javascript:alert(1)')).toBe('/');
    expect(safeRedirectPath(null)).toBe('/');
    expect(safeRedirectPath(undefined)).toBe('/');
    expect(safeRedirectPath(42)).toBe('/');
  });
});
