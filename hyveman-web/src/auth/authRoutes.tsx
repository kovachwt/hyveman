/** Route guard components (FRONTEND.md §5). */
import { Navigate, useLocation } from 'react-router-dom';
import type { ReactNode } from 'react';
import { Box, CircularProgress } from '@mui/material';
import { useAuth } from './AuthProvider';
import { redirectTarget, safeRedirectPath } from './guards';

export function FullPageLoader({ label = 'Loading…' }: { label?: string }) {
  return (
    <Box
      role="status"
      aria-label={label}
      sx={{
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        gap: 2,
        minHeight: '50vh',
        color: 'text.secondary',
      }}
    >
      <CircularProgress size={28} />
      <span>{label}</span>
    </Box>
  );
}

/** Gates protected application routes; unauthenticated visitors are sent to
 *  /login with the original internal path so login can return there. */
export function RequireAuth({ children }: { children: ReactNode }) {
  const { status } = useAuth();
  const location = useLocation();

  if (status === 'loading') return <FullPageLoader label="Checking session…" />;
  if (status === 'setup') return <Navigate to="/setup" replace />;
  if (status !== 'authenticated') {
    const from = location.pathname + location.search;
    return <Navigate to="/login" replace state={{ from: safeRedirectPath(from) }} />;
  }
  return children;
}

/** Renders the public pages, bouncing authenticated/setup visitors away.
 *  The redirect after login honors the originally requested internal path
 *  carried in location.state (set by RequireAuth), so a single deterministic
 *  navigation happens instead of racing the login handler. */
export function PublicOnly({ children }: { children: ReactNode }) {
  const { status } = useAuth();
  const location = useLocation();
  if (status === 'loading') return <FullPageLoader label="Checking session…" />;
  const target = redirectTarget(status, location.pathname);
  if (target) {
    const from = safeRedirectPath((location.state as { from?: unknown } | null)?.from);
    const destination = status === 'authenticated' && location.pathname === '/login' && from ? from : target;
    return <Navigate to={destination} replace />;
  }
  return children;
}
