import { describe, expect, it } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { mockApi } from '@/test/setup';
import { renderWithProviders } from '@/test/renderWithProviders';
import OverviewPage from './OverviewPage';

const overviewPayload = {
  generatedAt: '2025-08-09T14:00:00Z',
  hosts: [
    {
      id: 'hst_1',
      name: 'dc01',
      kind: 'windows-server',
      rollupState: 'critical',
      hardwareState: 'critical',
      osState: 'ok',
      hyperVState: 'ok',
      agent: { status: 'online', lastReceived: '2025-08-09T13:59:00Z', agentVersion: '1.2.3' },
      idrac: { configured: true, lastPoll: '2025-08-09T13:58:00Z', lastPollOk: true },
      activeAlertCount: 2,
    },
    {
      id: 'hst_2',
      name: 'web01',
      kind: 'linux-server',
      rollupState: 'warning',
      hardwareState: 'unknown',
      osState: 'warning',
      hyperVState: null,
      agent: { status: 'silent', lastReceived: '2025-08-09T08:00:00Z' },
      idrac: { configured: false },
      activeAlertCount: 0,
    },
  ],
  summary: { total: 2, ok: 0, warning: 1, critical: 1, unknown: 0, silentAgents: 1, activeAlerts: 2, unacknowledgedAlerts: 1 },
  recentAlerts: [{ id: 'a1', title: 'Disk critical on dc01', lastSeen: '2025-08-09T13:55:00Z' }],
};

function renderPage(route = '/') {
  return renderWithProviders(<OverviewPage />, { route });
}

describe('OverviewPage', () => {
  it('renders summary counts and host tiles with health badges', async () => {
    mockApi([{ path: '/api/v1/overview', respond: { body: overviewPayload } }]);
    renderPage();

    expect(await screen.findByText('dc01')).toBeInTheDocument();
    expect(screen.getByText('web01')).toBeInTheDocument();
    // Summary counts with accessible labels.
    expect(screen.getByLabelText('Hosts: 2')).toBeInTheDocument();
    expect(screen.getByLabelText('Critical: 1')).toBeInTheDocument();
    expect(screen.getByLabelText('Silent agents: 1')).toBeInTheDocument();
    // Health labels are text, never color-only.
    expect(screen.getAllByText('Warning').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Critical').length).toBeGreaterThan(0);
    expect(screen.getByText('2 active alerts')).toBeInTheDocument();
    expect(screen.getByText('No active alerts')).toBeInTheDocument();
  });

  it('shows an empty state when no hosts are registered', async () => {
    mockApi([{ path: '/api/v1/overview', respond: { body: { ...overviewPayload, hosts: [] } } }]);
    renderPage();
    expect(await screen.findByText('No hosts registered')).toBeInTheDocument();
  });

  it('shows the error state with retry on first load failure', async () => {
    mockApi([
      {
        path: '/api/v1/overview',
        respond: { status: 500, body: { type: 'about:blank', title: 'boom', status: 500, code: 'internal' } },
      },
    ]);
    renderPage();
    expect(await screen.findByTestId('error-state')).toBeInTheDocument();
  });

  it('keeps the last successful data and labels it stale when a refetch fails', async () => {
    const user = userEvent.setup();
    let fail = false;
    mockApi([
      {
        path: '/api/v1/overview',
        respond: () =>
          fail
            ? { status: 500, body: { title: 'boom', status: 500, code: 'internal' } }
            : { body: overviewPayload },
      },
    ]);
    const { queryClient } = renderPage();
    expect(await screen.findByText('dc01')).toBeInTheDocument();

    // A later refetch fails after the data was already shown.
    fail = true;
    void queryClient.invalidateQueries({ queryKey: ['/api/v1/overview'] });
    await waitFor(() => expect(screen.getByTestId('overview-stale-banner')).toBeInTheDocument());
    // Data stays visible and is not reset to an empty/healthy state.
    expect(screen.getByText('dc01')).toBeInTheDocument();
    expect(screen.queryByTestId('error-state')).not.toBeInTheDocument();

    // The banner offers a retry; retrying keeps the data visible.
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(screen.getByTestId('overview-stale-banner')).toBeInTheDocument());
    expect(screen.getByText('dc01')).toBeInTheDocument();
  });

  it('flags data as stale when the API timestamp is old', async () => {
    mockApi([
      {
        path: '/api/v1/overview',
        respond: {
          body: {
            ...overviewPayload,
            generatedAt: new Date(Date.now() - 5 * 60_000).toISOString(),
          },
        },
      },
    ]);
    renderPage();
    expect(await screen.findByTestId('overview-age-banner')).toBeInTheDocument();
  });
});
