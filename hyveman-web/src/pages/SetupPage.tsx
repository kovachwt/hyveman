/** First-run passkey registration (FRONTEND.md §7.3). The route is visible
 *  only when the API reports setup is required; the API remains the authority
 *  and can reject the ceremony from an untrusted network — we show that
 *  rejection without implying setup succeeded. */
import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Alert, Box, Button, Card, CardContent, Stack, TextField, Typography } from '@mui/material';
import ShieldOutlined from '@mui/icons-material/ShieldOutlined';
import { useAuth } from '@/auth/AuthProvider';
import {
  completeRegistration,
  PasskeyError,
  passkeysSupported,
  registerPasskey,
} from '@/auth/passkey';

export function SetupPage() {
  const { refresh } = useAuth();
  const navigate = useNavigate();
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const support = passkeysSupported();

  const handleSetup = async () => {
    setBusy(true);
    setError(null);
    try {
      const credential = await registerPasskey(name.trim() || undefined);
      await completeRegistration(credential);
      await refresh();
      navigate('/', { replace: true });
    } catch (err) {
      setError(err instanceof PasskeyError ? err.message : 'Registration failed. Please try again.');
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
      <Card sx={{ maxWidth: 480, width: '100%' }}>
        <CardContent sx={{ p: 4 }}>
          <Stack spacing={2.5}>
            <Stack direction="row" spacing={1.5} alignItems="center">
              <ShieldOutlined sx={{ fontSize: 36, color: 'primary.main' }} />
              <Box>
                <Typography variant="h5" component="h1">First-run setup</Typography>
                <Typography variant="body2" color="text.secondary">
                  Create the administrator passkey for this Hyveman instance.
                </Typography>
              </Box>
            </Stack>

            <Alert severity="warning">
              Passkey registration is only permitted from the trusted setup network (loopback by
              default). If this request comes from elsewhere the API will reject it — that rejection
              is shown here and setup does not proceed.
            </Alert>

            {!support.ok ? (
              <Alert severity="warning">{support.reason}</Alert>
            ) : null}

            <TextField
              label="Passkey name (optional)"
              value={name}
              onChange={(e) => setName(e.target.value)}
              helperText="A friendly label, e.g. “Work laptop”."
              disabled={busy}
              fullWidth
            />

            {error ? (
              <Alert severity="error" data-testid="setup-error">
                {error}
              </Alert>
            ) : null}

            <Button
              variant="contained"
              size="large"
              disabled={busy || !support.ok}
              onClick={() => void handleSetup()}
              data-testid="setup-button"
            >
              {busy ? 'Waiting for passkey…' : 'Register passkey'}
            </Button>
          </Stack>
        </CardContent>
      </Card>
    </Box>
  );
}

