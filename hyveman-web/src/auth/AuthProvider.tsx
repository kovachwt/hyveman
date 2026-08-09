/**
 * Session bootstrap and route gating (FRONTEND.md §7.1). The session is an
 * opaque HttpOnly cookie owned by the API; this provider only ever asks
 * GET /auth/session and reacts to the response. A protected route is never
 * rendered merely because an old session query is cached.
 */
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  type ReactNode,
} from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { getApiV1AuthSession, postApiV1AuthLogout } from '@/api/generated/endpoints';
import { queryKeys } from '@/api/queryKeys';
import { setUnauthorizedHandler } from '@/api/client';
import { sessionStatus, type AuthStatus } from './guards';

interface AuthContextValue {
  status: AuthStatus;
  /** Session response; null until the first bootstrap completes. */
  session: Awaited<ReturnType<typeof getApiV1AuthSession>>['data'] | null;
  /** Re-runs GET /auth/session (after login/setup ceremonies). */
  refresh: () => Promise<void>;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();

  const sessionQuery = useQuery({
    queryKey: queryKeys.session(),
    queryFn: () => getApiV1AuthSession(),
    staleTime: 0,
    retry: 1,
    select: (res) => res.data,
  });

  const refresh = useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey: queryKeys.session() });
  }, [queryClient]);

  // Any 401 from the API (expired/revoked session) invalidates the session
  // query so the router can bounce the user to /login.
  useEffect(() => {
    setUnauthorizedHandler(() => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.session() });
    });
    return () => setUnauthorizedHandler(null);
  }, [queryClient]);

  const logout = useCallback(async () => {
    try {
      await postApiV1AuthLogout();
    } catch {
      // The session may already be gone; clear local state regardless.
    }
    // The session query drives the router: refetch it so the app settles on
    // /login deterministically. (queryClient.clear() alone destroys the
    // mounted session query without notifying its observer in v5, which would
    // leave a stale authenticated status behind.) Then drop every other
    // cached resource so nothing lingers across sessions.
    await queryClient.invalidateQueries({ queryKey: queryKeys.session() });
    queryClient.removeQueries({
      predicate: (query) => query.queryKey[0] !== '/api/v1/auth/session',
    });
  }, [queryClient]);

  const status = sessionStatus(sessionQuery.data, sessionQuery.isPending);

  const value = useMemo<AuthContextValue>(
    () => ({ status, session: sessionQuery.data ?? null, refresh, logout }),
    [status, sessionQuery.data, refresh, logout],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}

export function useSession() {
  const { status, session } = useAuth();
  return { status, session };
}
