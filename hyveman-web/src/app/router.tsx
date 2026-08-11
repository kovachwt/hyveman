/** Route table (FRONTEND.md §5): public auth routes plus the authenticated
 *  application routes, lazy-loaded per feature for route-level code splitting
 *  (§12) — the dashboard loads independently of event-search/charting code. */
import { lazy, Suspense } from 'react';
import { createBrowserRouter, Navigate } from 'react-router-dom';
import { AppShell } from '@/components/AppShell/AppShell';
import { FullPageLoader, PublicOnly, RequireAuth } from '@/auth/authRoutes';
import { LoginPage } from '@/pages/LoginPage';
import { SetupPage } from '@/pages/SetupPage';
import { AcceptInvitePage } from '@/pages/AcceptInvitePage';

const OverviewPage = lazy(() => import('@/features/overview/OverviewPage'));
const HostsPage = lazy(() => import('@/features/hosts/HostsPage'));
const HostDetailPage = lazy(() => import('@/features/hosts/HostDetailPage'));
const LogonStatsPage = lazy(() => import('@/features/logons/LogonStatsPage'));
const LogsPage = lazy(() => import('@/features/events/LogsPage'));
const AlertsPage = lazy(() => import('@/features/alerts/AlertsPage'));
const RulesPage = lazy(() => import('@/features/rules/RulesPage'));
const NotificationsPage = lazy(() => import('@/features/notifications/NotificationsPage'));
const MaintenancePage = lazy(() => import('@/features/maintenance/MaintenancePage'));
const SourcesPage = lazy(() => import('@/features/sources/SourcesPage'));
const RetentionPage = lazy(() => import('@/features/settings/RetentionPage'));
const AuditPage = lazy(() => import('@/features/audit/AuditPage'));
const PasskeysPage = lazy(() => import('@/features/passkeys/PasskeysPage'));
const UsersPage = lazy(() => import('@/features/users/UsersPage'));

function withSuspense(element: React.ReactNode) {
  return <Suspense fallback={<FullPageLoader />}>{element}</Suspense>;
}

export const router = createBrowserRouter([
  {
    path: '/login',
    element: (
      <PublicOnly>
        <LoginPage />
      </PublicOnly>
    ),
  },
  {
    path: '/setup',
    element: (
      <PublicOnly>
        <SetupPage />
      </PublicOnly>
    ),
  },
  {
    path: '/accept-invite',
    element: (
      <PublicOnly>
        <AcceptInvitePage />
      </PublicOnly>
    ),
  },
  {
    path: '/',
    element: (
      <RequireAuth>
        <AppShell />
      </RequireAuth>
    ),
    children: [
      { index: true, element: withSuspense(<OverviewPage />) },
      { path: 'hosts', element: withSuspense(<HostsPage />) },
      { path: 'hosts/:hostId', element: withSuspense(<HostDetailPage />) },
      { path: 'hosts/:hostId/logons', element: withSuspense(<LogonStatsPage />) },
      { path: 'logs', element: withSuspense(<LogsPage />) },
      { path: 'alerts', element: withSuspense(<AlertsPage />) },
      { path: 'rules', element: withSuspense(<RulesPage />) },
      { path: 'notifications', element: withSuspense(<NotificationsPage />) },
      { path: 'maintenance', element: withSuspense(<MaintenancePage />) },
      { path: 'admin/sources', element: withSuspense(<SourcesPage />) },
      { path: 'admin/users', element: withSuspense(<UsersPage />) },
      { path: 'admin/retention', element: withSuspense(<RetentionPage />) },
      { path: 'admin/audit', element: withSuspense(<AuditPage />) },
      { path: 'admin/passkeys', element: withSuspense(<PasskeysPage />) },
    ],
  },
  {
    path: '*',
    element: <NotFoundPage />,
  },
]);

function NotFoundPage() {
  return <Navigate to="/" replace />;
}
