import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { ThemeProvider } from '@mui/material';
import { CssBaseline } from '@mui/material';
import { render } from '@testing-library/react';
import { mockApi } from '@/test/setup';
import { buildTheme } from '@/app/theme';
import { AuthProvider } from '@/auth/AuthProvider';
import { AcceptInvitePage } from './AcceptInvitePage';

function renderInvite(entries: string[] = ['/accept-invite#token=inv_test']) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={entries}>
        <AuthProvider>
          <ThemeProvider theme={buildTheme('light')}>
            <CssBaseline />
            <AcceptInvitePage />
          </ThemeProvider>
        </AuthProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function secureContext() {
  Object.defineProperty(window, 'isSecureContext', { value: true, configurable: true });
  Object.defineProperty(window, 'PublicKeyCredential', { value: class {}, configurable: true });
}

describe('AcceptInvitePage', () => {
  it('validates the invite token and requires a username before accepting', async () => {
    secureContext();
    mockApi([
      { path: '/api/v1/auth/session', respond: { body: { authenticated: false, setupRequired: false, user: null } } },
      {
        path: '/api/v1/auth/invitations/inspect',
        method: 'POST',
        respond: { body: { valid: true, createdBy: 'alice', expiresAt: null } },
      },
    ]);
    renderInvite();
    expect(await screen.findByTestId('invite-valid')).toHaveTextContent(/invited by alice/);
    // Username too short → accept stays disabled.
    expect(screen.getByRole('button', { name: 'Create account' })).toBeDisabled();
    await userEvent.type(screen.getByLabelText(/Username/), 'bob');
    expect(screen.getByRole('button', { name: 'Create account' })).toBeEnabled();
  });

  it('shows the invalid-invite state for a bogus token, without implying success', async () => {
    secureContext();
    mockApi([
      { path: '/api/v1/auth/session', respond: { body: { authenticated: false, setupRequired: false, user: null } } },
      {
        path: '/api/v1/auth/invitations/inspect',
        method: 'POST',
        respond: { body: { valid: false } },
      },
    ]);
    renderInvite(['/accept-invite#token=inv_bogus']);
    expect(await screen.findByTestId('invite-invalid')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create account' })).toBeDisabled();
  });

  it('reports a missing token fragment as invalid', async () => {
    secureContext();
    mockApi([
      { path: '/api/v1/auth/session', respond: { body: { authenticated: false, setupRequired: false, user: null } } },
    ]);
    renderInvite(['/accept-invite']);
    expect(await screen.findByTestId('invite-invalid')).toBeInTheDocument();
  });
});
