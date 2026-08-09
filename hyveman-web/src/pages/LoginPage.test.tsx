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
import { LoginPage } from './LoginPage';

function renderLogin() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/login']}>
        <AuthProvider>
          <ThemeProvider theme={buildTheme('light')}>
            <CssBaseline />
            <LoginPage />
          </ThemeProvider>
        </AuthProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('LoginPage', () => {
  it('reports unsupported browsers/security contexts clearly (no silent fallback)', async () => {
    mockApi([
      { path: '/api/v1/auth/session', respond: { body: { authenticated: false, setupRequired: false, adminName: null } } },
    ]);
    // jsdom defaults isSecureContext=false, so the page must say so.
    renderLogin();
    expect(await screen.findByText(/secure context/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Sign in with passkey/ })).toBeDisabled();
  });

  it('shows a clear error when the options request fails, with a retry action', async () => {
    Object.defineProperty(window, 'isSecureContext', { value: true, configurable: true });
    Object.defineProperty(window, 'PublicKeyCredential', { value: class {}, configurable: true });
    mockApi([
      { path: '/api/v1/auth/session', respond: { body: { authenticated: false, setupRequired: false, adminName: null } } },
      {
        path: '/api/v1/auth/passkeys/login/options',
        method: 'POST',
        respond: { status: 503, body: { title: 'Unavailable', status: 503, code: 'unavailable' } },
      },
    ]);
    const user = userEvent.setup();
    renderLogin();
    const button = await screen.findByRole('button', { name: /Sign in with passkey/ });
    await user.click(button);
    expect(await screen.findByTestId('login-error')).toBeInTheDocument();
    // The sign-in button remains available for a retry.
    expect(screen.getByRole('button', { name: /Sign in with passkey/ })).toBeEnabled();
  });
});
