import {
  createContext,
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

type ThemeMode = 'light' | 'dark' | 'system';
interface ThemeModeContextValue {
  /** The user's selection — may be 'system', which resolves per OS below. */
  mode: ThemeMode;
  /** Concrete 'light' | 'dark' the app actually renders with. `buildTheme`
   *  takes a concrete mode, so 'system' is never passed down here. */
  resolvedMode: 'light' | 'dark';
  setMode: (mode: ThemeMode) => void;
}
const ThemeModeContext = createContext<ThemeModeContextValue>({
  mode: 'system',
  resolvedMode: 'light',
  setMode: () => undefined,
});

const THEME_STORAGE_KEY = 'hyveman-theme-mode';

function initialMode(): ThemeMode {
  const stored = localStorage.getItem(THEME_STORAGE_KEY);
  if (stored === 'light' || stored === 'dark' || stored === 'system') return stored;
  // No stored preference: follow the OS rather than forcing light. A user who
  // pins a mode explicitly still wins, and 'system' re-evaluates live.
  return 'system';
}

function prefersDark(): boolean {
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false;
}

function ThemeModeProvider({ children }: { children: ReactNode }) {
  const [mode, setMode] = useState<ThemeMode>(initialMode);
  // Track the OS preference so 'system' follows live changes rather than the
  // snapshot taken at boot: switch the OS theme while the console is open and
  // the app follows when in 'system' mode.
  const [systemDark, setSystemDark] = useState<boolean>(prefersDark);

  const resolvedMode: 'light' | 'dark' =
    mode === 'system' ? (systemDark ? 'dark' : 'light') : mode;

  useEffect(() => {
    localStorage.setItem(THEME_STORAGE_KEY, mode);
  }, [mode]);

  useEffect(() => {
    if (mode !== 'system') return;
    const mq = window.matchMedia('(prefers-color-scheme: dark)');
    // Re-sync when entering system mode (the OS may have flipped since boot).
    setSystemDark(mq.matches);
    const handler = (e: MediaQueryListEvent) => setSystemDark(e.matches);
    mq.addEventListener('change', handler);
    return () => mq.removeEventListener('change', handler);
  }, [mode]);

  const value = useMemo<ThemeModeContextValue>(
    () => ({ mode, resolvedMode, setMode }),
    [mode, resolvedMode],
  );
  return <ThemeModeContext.Provider value={value}>{children}</ThemeModeContext.Provider>;
}

export function useThemeMode(): ThemeModeContextValue {
  return useContext(ThemeModeContext);
}

function ThemedRoot({ children }: { children: ReactNode }) {
  const { resolvedMode } = useThemeMode();
  return (
    <ThemeProvider theme={buildTheme(resolvedMode)}>
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