import { createTheme, type Theme } from '@mui/material/styles';

declare module '@mui/material/styles' {
  interface Theme {
    health: {
      ok: string;
      warning: string;
      critical: string;
      neutral: string;
    };
  }
  interface ThemeOptions {
    health?: {
      ok: string;
      warning: string;
      critical: string;
      neutral: string;
    };
  }
}

/** MUI theme variants for light/dark display. Health accents are reinforced by
 *  labels/icons in the UI (lib/health.ts); the theme only supplies colors. */
export function buildTheme(mode: 'light' | 'dark'): Theme {
  return createTheme({
    palette: {
      mode,
      primary: { main: mode === 'dark' ? '#90caf9' : '#1565c0' },
      secondary: { main: mode === 'dark' ? '#ce93d8' : '#6a1b9a' },
    },
    health:
      mode === 'dark'
        ? { ok: '#66bb6a', warning: '#ffb74d', critical: '#ef5350', neutral: '#9e9e9e' }
        : { ok: '#1b5e20', warning: '#b45309', critical: '#b71c1c', neutral: '#616161' },
    shape: { borderRadius: 8 },
    components: {
      MuiButton: { defaultProps: { disableElevation: true } },
      MuiCard: {
        styleOverrides: {
          root: ({ theme }) => ({
            border: `1px solid ${theme.palette.divider}`,
            boxShadow: 'none',
          }),
        },
      },
      MuiChip: {
        styleOverrides: {
          root: ({ theme }) => ({
            fontWeight: 600,
            backgroundColor: theme.palette.action.selected,
          }),
        },
      },
      MuiTableRow: {
        styleOverrides: {
          root: ({ theme }) => ({
            '&:hover': { backgroundColor: theme.palette.action.hover },
          }),
        },
      },
    },
    typography: {
      fontSize: 14,
      h4: { fontWeight: 600 },
      h5: { fontWeight: 600 },
      h6: { fontWeight: 600 },
      button: { textTransform: 'none' },
    },
  });
}
