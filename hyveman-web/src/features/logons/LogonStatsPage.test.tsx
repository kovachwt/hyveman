import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { ThemeProvider } from '@mui/material';
import { CssBaseline } from '@mui/material';
import { mockApi } from '@/test/setup';
import { buildTheme } from '@/app/theme';
import { renderWithProviders } from '@/test/renderWithProviders';
import LogonStatsPage from './LogonStatsPage';

const statsPayload = {
  items: [
    { day: '2025-08-09', sourceId: 's1', sourceName: 'dc01', user: 'alice', logonType: 2, successCount: 3, failureCount: 0 },
    { day: '2025-08-09', sourceId: 's1', sourceName: 'dc01', user: 'bob', logonType: 10, successCount: 1, failureCount: 2 },
    { day: '2025-08-10', sourceId: 's1', sourceName: 'dc01', user: 'carol', logonType: null, successCount: 0, failureCount: 1 },
  ],
  hasMore: true,
};

const route = '/logon-stats?from=2025-08-01&to=2025-08-10';

describe('LogonStatsPage', () => {
  it('renders the summary strip, table with UTC-day labels, and bounded-result notice', async () => {
    mockApi([
      {
        path: '/api/v1/logon-stats',
        respond: (url) => {
          expect(url).toContain('from=2025-08-01');
          expect(url).toContain('to=2025-08-10');
          return { body: { data: statsPayload } };
        },
      },
    ]);
    renderWithProviders(<LogonStatsPage />, { route });

    // Two rows share the UTC day; both carry the explicit UTC label.
    const utcDayCells = await screen.findAllByText('2025-08-09 (UTC)');
    expect(utcDayCells.length).toBe(2);
    expect(screen.getAllByText('2025-08-10 (UTC)').length).toBeGreaterThan(0);
    expect(screen.getByText('alice')).toBeInTheDocument();
    // Logon type labels (2 = Interactive, 10 = RDP, null = Lockout).
    expect(screen.getByText('Interactive')).toBeInTheDocument();
    expect(screen.getByText('Remote Interactive (RDP)')).toBeInTheDocument();
    expect(screen.getByText('Lockout')).toBeInTheDocument();
    // Summary strip: 4 successes, 3 failures, 1 lockout (accessible labels).
    expect(screen.getByLabelText('Successful logons: 4')).toBeInTheDocument();
    expect(screen.getByLabelText('Failed logons: 3')).toBeInTheDocument();
    expect(screen.getByLabelText('Lockouts: 1')).toBeInTheDocument();
    // Bounded-result notice (no cursor exists; API caps page size).
    expect(screen.getAllByText(/no cursor/).length).toBeGreaterThan(0);
  });

  it('shows an empty state when there are no rows', async () => {
    mockApi([{ path: '/api/v1/logon-stats', respond: { body: { data: { items: [], hasMore: false } } } }]);
    renderWithProviders(<LogonStatsPage />, { route });
    expect(await screen.findByText(/No logon rows in range/)).toBeInTheDocument();
  });

  it('shows the error state with retry on failure', async () => {
    mockApi([
      {
        path: '/api/v1/logon-stats',
        respond: { status: 500, body: { title: 'boom', status: 500, code: 'internal' } },
      },
    ]);
    renderWithProviders(<LogonStatsPage />, { route });
    expect(await screen.findByTestId('error-state')).toBeInTheDocument();
  });

  it('explains that a host without an associated source yields no rows', async () => {
    mockApi([
      {
        path: '/api/v1/hosts/hst_1',
        respond: {
          body: {
            data: { id: 'hst_1', name: 'bare-metal', kind: 'windows-server', sourceId: null, enabled: true },
          },
        },
      },
    ]);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 0 } } });
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/hosts/hst_1/logons']}>
          <ThemeProvider theme={buildTheme('light')}>
            <CssBaseline />
            <Routes>
              <Route path="/hosts/:hostId/logons" element={<LogonStatsPage />} />
            </Routes>
          </ThemeProvider>
        </MemoryRouter>
      </QueryClientProvider>,
    );
    expect(await screen.findByText('Host has no agent source')).toBeInTheDocument();
  });
});
