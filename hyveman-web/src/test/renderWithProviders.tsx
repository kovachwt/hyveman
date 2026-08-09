import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { ThemeProvider } from '@mui/material';
import { CssBaseline } from '@mui/material';
import { render, type RenderResult } from '@testing-library/react';
import { buildTheme } from '@/app/theme';
import type { ReactNode } from 'react';

/**
 * Test render helper: query cache + router + MUI theme. Feature pages that
 * need auth state should mount AuthProvider themselves (see pages tests).
 */
export function renderWithProviders(
  ui: ReactNode,
  { route = '/', queryClient }: { route?: string; queryClient?: QueryClient } = {},
): RenderResult & { queryClient: QueryClient } {
  const client =
    queryClient ??
    new QueryClient({
      defaultOptions: {
        queries: { retry: false, staleTime: 0 },
        mutations: { retry: false },
      },
    });

  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[route]}>
          <ThemeProvider theme={buildTheme('light')}>
            <CssBaseline />
            {children}
          </ThemeProvider>
        </MemoryRouter>
      </QueryClientProvider>
    );
  }

  const result = render(ui, { wrapper: Wrapper });
  return { ...result, queryClient: client };
}
