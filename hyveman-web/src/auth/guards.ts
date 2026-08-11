/**
 * Pure route-gating decisions (unit-tested in auth/guards.test.ts). The API is
 * the authority on session/setup state; these helpers only translate that
 * state into navigation targets. Redirect targets are always internal paths.
 */
import type { SessionResponse } from '@/api/generated/endpoints';

export type AuthStatus = 'loading' | 'setup' | 'anonymous' | 'authenticated';

/** Maps a session response to the bootstrap status (FRONTEND.md §5). */
export function sessionStatus(
  session: SessionResponse | undefined,
  loading: boolean,
): AuthStatus {
  if (loading) return 'loading';
  if (session?.authenticated) return 'authenticated';
  if (session?.setupRequired) return 'setup';
  return 'anonymous';
}

/** Where a visitor at `path` should go given the auth status, or null when the
 *  current page is allowed. Redirect targets are internal paths only. */
export function redirectTarget(status: AuthStatus, path: string): string | null {
  if (status === 'loading') return null;
  if (path === '/login') {
    if (status === 'authenticated') return '/';
    if (status === 'setup') return '/setup';
    return null;
  }
  if (path === '/setup') {
    if (status === 'authenticated') return '/';
    if (status === 'setup') return null;
    return '/login';
  }
  if (path === '/accept-invite') {
    if (status === 'authenticated') return '/';
    if (status === 'setup') return '/setup';
    return null;
  }
  // Protected application routes.
  if (status === 'setup') return '/setup';
  if (status !== 'authenticated') return '/login';
  return null;
}

/** Login redirect must be an internal path; anything else (external URLs,
 *  protocol-relative) falls back to the dashboard. */
export function safeRedirectPath(target: unknown): string {
  if (typeof target !== 'string') return '/';
  if (!target.startsWith('/') || target.startsWith('//')) return '/';
  return target;
}
