import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CssBaseline, ThemeProvider } from '@mui/material';
import { buildTheme } from './theme';
import { AuthProvider } from '@/auth/AuthProvider';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: 1,
      staleTime: 15_000,
      refetchOnWindowFocus: true,
    },
    mutations: {
      retry: 0,
    },
  },
});

export { queryClient };

// ─── Theme mode (light / dark / system) ─────────────────────────────────────

type ThemeMode = 'light' | 'dark';
interface ThemeModeContextValue {
  mode: ThemeMode;
  toggleMode: () => void;
}
const ThemeModeContext = createContext<ThemeModeContextValue>({
  mode: 'light',
  toggleMode: () => undefined,
});

const THEME_STORAGE_KEY = 'hyveman-theme-mode';

function initialMode(): ThemeMode {
  const stored = localStorage.getItem(THEME_STORAGE_KEY);
  if (stored === 'light' || stored === 'dark') return stored;
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

function ThemeModeProvider({ children }: { children: ReactNode }) {
  const [mode, setMode] = useState<ThemeMode>(initialMode);
  useEffect(() => {
    localStorage.setItem(THEME_STORAGE_KEY, mode);
  }, [mode]);
  const toggleMode = useCallback(() => setMode((m) => (m === 'light' ? 'dark' : 'light')), []);
  const value = useMemo(() => ({ mode, toggleMode }), [mode, toggleMode]);
  return <ThemeModeContext.Provider value={value}>{children}</ThemeModeContext.Provider>;
}

export function useThemeMode(): ThemeModeContextValue {
  return useContext(ThemeModeContext);
}

function ThemedRoot({ children }: { children: ReactNode }) {
  const { mode } = useThemeMode();
  return (
    <ThemeProvider theme={buildTheme(mode)}>
      <CssBaseline />
      {children}
    </ThemeProvider>
  );
}

// ─── Provider stack (FRONTEND.md §4): server state, auth, theme ─────────────

export function AppProviders({ children }: { children: ReactNode }) {
  return (
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        <ThemeModeProvider>
          <ThemedRoot>{children}</ThemedRoot>
        </ThemeModeProvider>
      </AuthProvider>
    </QueryClientProvider>
  );
}
