/**
 * Host list filters (FRONTEND.md §5: "/hosts — Host list and filters"). The
 * hosts endpoint has no server-side filters, so filtering is client-side for
 * the small fleet and persisted in the URL like the other list pages.
 */
import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { mockApi } from '@/test/setup';
import { renderWithProviders } from '@/test/renderWithProviders';
import HostsPage from './HostsPage';

const hostsPayload = [
  {
    id: 'hst_1',
    name: 'dc01',
    kind: 'windows-server',
    sourceId: 'src_1',
    idracUrl: 'https://idrac-dc01.internal',
    idracCredentialSet: true,
    enabled: true,
    notes: 'domain controller',
    createdAt: '2025-01-01T00:00:00Z',
    updatedAt: '2025-01-02T00:00:00Z',
  },
  {
    id: 'hst_2',
    name: 'web01',
    kind: 'linux-server',
    sourceId: 'src_2',
    idracUrl: null,
    idracCredentialSet: false,
    enabled: true,
    notes: null,
    createdAt: '2025-01-01T00:00:00Z',
    updatedAt: '2025-01-02T00:00:00Z',
  },
  {
    id: 'hst_3',
    name: 'old-db',
    kind: 'windows-server',
    sourceId: 'src_3',
    idracUrl: null,
    idracCredentialSet: false,
    enabled: false,
    notes: 'decommissioning',
    createdAt: '2025-01-01T00:00:00Z',
    updatedAt: '2025-01-02T00:00:00Z',
  },
];

function renderPage(route = '/hosts') {
  return renderWithProviders(<HostsPage />, { route });
}

describe('HostsPage filters', () => {
  it('lists all hosts and filters by free text', async () => {
    const user = userEvent.setup();
    mockApi([
      { path: '/api/v1/sources', respond: { body: [] } },
      { path: '/api/v1/hosts', respond: { body: hostsPayload } },
    ]);
    renderPage();

    expect(await screen.findByText('dc01')).toBeInTheDocument();
    expect(screen.getByText('web01')).toBeInTheDocument();
    expect(screen.getByText('old-db')).toBeInTheDocument();

    await user.type(screen.getByLabelText(/Search hosts/), 'old');
    expect(screen.queryByText('dc01')).not.toBeInTheDocument();
    expect(screen.getByText('old-db')).toBeInTheDocument();
    expect(screen.getByText(/Showing 1 of 3 hosts/)).toBeInTheDocument();
  });

  it('filters by kind and enabled state, with a clear action', async () => {
    const user = userEvent.setup();
    mockApi([
      { path: '/api/v1/sources', respond: { body: [] } },
      { path: '/api/v1/hosts', respond: { body: hostsPayload } },
    ]);
    renderPage();
    await screen.findByText('dc01');

    // Kind: only linux-server remains.
    await user.click(screen.getByRole('combobox', { name: 'Kind' }));
    await user.click(await screen.findByRole('option', { name: 'linux-server' }));
    expect(screen.getByText('web01')).toBeInTheDocument();
    expect(screen.queryByText('dc01')).not.toBeInTheDocument();
    expect(screen.queryByText('old-db')).not.toBeInTheDocument();

    // Enabled=Disabled narrows to nothing (old-db is windows-server).
    await user.click(screen.getByRole('combobox', { name: 'Enabled' }));
    await user.click(await screen.findByRole('option', { name: 'Disabled' }));
    expect(screen.getByText(/No hosts match your filters/)).toBeInTheDocument();

    // Clearing restores the full list (the empty state's action clears too;
    // the filter bar has an identical button).
    const clearButtons = screen.getAllByRole('button', { name: 'Clear filters' });
    await user.click(clearButtons[clearButtons.length - 1]!);
    expect(screen.getByText('dc01')).toBeInTheDocument();
    expect(screen.getByText('web01')).toBeInTheDocument();
    expect(screen.getByText('old-db')).toBeInTheDocument();
    expect(screen.queryByText(/Showing /)).not.toBeInTheDocument();
  });

  it('restores filters from the URL (shareable links)', async () => {
    mockApi([
      { path: '/api/v1/sources', respond: { body: [] } },
      { path: '/api/v1/hosts', respond: { body: hostsPayload } },
    ]);
    renderPage('/hosts?kind=linux-server&enabled=true');

    expect(await screen.findByText('web01')).toBeInTheDocument();
    expect(screen.queryByText('dc01')).not.toBeInTheDocument();
    expect(screen.queryByText('old-db')).not.toBeInTheDocument();
    expect(screen.getByText(/Showing 1 of 3 hosts/)).toBeInTheDocument();
  });
});
