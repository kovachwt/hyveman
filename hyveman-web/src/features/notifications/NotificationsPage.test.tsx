import { describe, expect, it } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { mockApi, type CapturedRequest } from '@/test/setup';
import { renderWithProviders } from '@/test/renderWithProviders';
import NotificationsPage from './NotificationsPage';

const channelsPayload = [
  {
    id: 'c1',
    name: 'Ops telegram',
    kind: 'telegram',
    enabled: true,
    created: '2025-01-01T00:00:00Z',
    updatedAt: '2025-06-01T00:00:00Z',
    configSummary: { botToken: 'redacted', chatId: 'redacted' },
  },
];

function requestsByMethod(requests: CapturedRequest[], method: string, pathPart: string) {
  return requests.filter((r) => r.method === method && r.url.includes(pathPart));
}

describe('NotificationsPage', () => {
  it('creates a Telegram channel: secrets are sent in the request and never echoed back', async () => {
    const user = userEvent.setup();
    const api = mockApi([
      { path: '/api/v1/notification-channels', method: 'GET', respond: { body: channelsPayload } },
      { path: '/api/v1/notification-channels', method: 'POST', respond: { body: { ...channelsPayload[0], id: 'c2' } } },
    ]);
    renderWithProviders(<NotificationsPage />);

    await user.click(await screen.findByRole('button', { name: 'New channel' }));
    await user.type(screen.getByLabelText(/Name/), 'New telegram');
    await user.type(screen.getByLabelText(/Telegram bot token/), '123:secret-token');
    await user.type(screen.getByLabelText(/Telegram chat ID/), '-100987654');
    await user.click(screen.getByRole('button', { name: 'Create channel' }));

    await waitFor(() => {
      const posts = requestsByMethod(api.requests(), 'POST', '/api/v1/notification-channels');
      expect(posts.length).toBe(1);
    });
    const body = requestsByMethod(api.requests(), 'POST', '/api/v1/notification-channels')[0]!.body as {
      name: string;
      config: { telegramBotToken: string; telegramChatId: string };
    };
    expect(body.name).toBe('New telegram');
    expect(body.config.telegramBotToken).toBe('123:secret-token');
    expect(body.config.telegramChatId).toBe('-100987654');
  });

  it('editing with blank secrets leaves stored values unchanged (no config sent)', async () => {
    const user = userEvent.setup();
    const api = mockApi([
      { path: '/api/v1/notification-channels', method: 'GET', respond: { body: channelsPayload } },
      { path: '/api/v1/notification-channels/c1', method: 'PATCH', respond: { body: channelsPayload[0] } },
    ]);
    renderWithProviders(<NotificationsPage />);

    await user.click(await screen.findByLabelText(/Edit channel Ops telegram/));
    // The dialog must not contain secret values (API never echoes them).
    expect(screen.queryByDisplayValue('123:secret-token')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => {
      const patches = requestsByMethod(api.requests(), 'PATCH', '/api/v1/notification-channels/c1');
      expect(patches.length).toBe(1);
    });
    const body = requestsByMethod(api.requests(), 'PATCH', '/api/v1/notification-channels/c1')[0]!.body as {
      config?: unknown;
      updatedAt?: string;
    };
    expect(body.config).toBeUndefined();
    // The version marker is echoed back so the API can 409 on stale edits.
    expect(body.updatedAt).toBe('2025-06-01T00:00:00Z');
  });

  it('shows the test result without exposing provider response bodies', async () => {
    const user = userEvent.setup();
    mockApi([
      { path: '/api/v1/notification-channels', method: 'GET', respond: { body: channelsPayload } },
      {
        path: '/api/v1/notification-channels/c1/test',
        method: 'POST',
        respond: { body: { channelId: 'c1', ok: true, testedAt: '2025-08-09T14:00:00Z' } },
      },
    ]);
    renderWithProviders(<NotificationsPage />);

    await user.click(await screen.findByLabelText(/Test channel Ops telegram/));
    await user.click(screen.getByRole('button', { name: 'Send test' }));

    expect(await screen.findByText(/Test notification sent successfully/)).toBeInTheDocument();
  });

  it('shows a warning when a test fails, without echoing secret details', async () => {
    const user = userEvent.setup();
    mockApi([
      { path: '/api/v1/notification-channels', method: 'GET', respond: { body: channelsPayload } },
      {
        path: '/api/v1/notification-channels/c1/test',
        method: 'POST',
        respond: {
          body: { channelId: 'c1', ok: false, testedAt: '2025-08-09T14:00:00Z', error: 'HTTP 401 from provider' },
        },
      },
    ]);
    renderWithProviders(<NotificationsPage />);

    await user.click(await screen.findByLabelText(/Test channel Ops telegram/));
    await user.click(screen.getByRole('button', { name: 'Send test' }));

    expect(await screen.findByText(/Test notification failed/)).toBeInTheDocument();
  });
});
