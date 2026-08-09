/** Passkey-only login (FRONTEND.md §7.2). No password/TOTP/backup-code
 *  fallback exists; errors explain browser/security-context problems clearly. */
import { useState } from 'react';
import { Alert, Box, Button, Card, CardContent, Stack, Typography } from '@mui/material';
import KeyIcon from '@mui/icons-material/Key';
import { useAuth } from '@/auth/AuthProvider';
import {
  authenticateWithPasskey,
  completeLogin,
  PasskeyError,
  passkeysSupported,
} from '@/auth/passkey';

export function LoginPage() {
  const { refresh } = useAuth();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const support = passkeysSupported();

  const handleLogin = async () => {
    setBusy(true);
    setError(null);
    try {
      const credential = await authenticateWithPasskey();
      await completeLogin(credential);
      // PublicOnly performs the redirect to the originally requested route
      // once the refreshed session reports authenticated — no race here.
      await refresh();
    } catch (err) {
      setError(err instanceof PasskeyError ? err.message : 'Sign-in failed. Please try again.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Box
      sx={{
        minHeight: '100vh',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        p: 2,
        bgcolor: 'background.default',
      }}
    >
      <Card sx={{ maxWidth: 420, width: '100%' }}>
        <CardContent sx={{ p: 4 }}>
          <Stack spacing={2.5} alignItems="center" textAlign="center">
            <KeyIcon sx={{ fontSize: 44, color: 'primary.main' }} />
            <Box>
              <Typography variant="h5" component="h1">Hyveman</Typography>
              <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
                Sign in with a passkey to open the operations console.
              </Typography>
            </Box>

            {!support.ok ? (
              <Alert severity="warning" sx={{ width: '100%', textAlign: 'left' }}>
                {support.reason} Use a supported browser over HTTPS (or localhost) and try again.
              </Alert>
            ) : null}

            {error ? (
              <Alert severity="error" sx={{ width: '100%', textAlign: 'left' }} data-testid="login-error">
                {error}
              </Alert>
            ) : null}

            <Button
              variant="contained"
              size="large"
              fullWidth
              disabled={busy || !support.ok}
              onClick={() => void handleLogin()}
              data-testid="login-button"
            >
              {busy ? 'Waiting for passkey…' : 'Sign in with passkey'}
            </Button>
          </Stack>
        </CardContent>
      </Card>
    </Box>
  );
}

