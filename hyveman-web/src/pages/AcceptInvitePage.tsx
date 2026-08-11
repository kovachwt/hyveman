/**
 * Invite acceptance (docs/MULTI-USER.md §7): the invitee opens the shareable
 * /accept-invite#token=... link, picks a username + passkey name, and runs
 * the registration ceremony with the invite token. The API creates the new
 * user + passkey, consumes the invite and issues a session. The token rides
 * only in the URL fragment and request body — never query strings or logs.
 */
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import PersonAddAlt1Outlined from '@mui/icons-material/PersonAddAlt1Outlined';
import { useAuth } from '@/auth/AuthProvider';
import {
  completeRegistration,
  PasskeyError,
  passkeysSupported,
  registerPasskey,
} from '@/auth/passkey';
import { postApiV1AuthInvitationsInspect } from '@/api/generated/endpoints';

type InspectState =
  | { kind: 'idle' }
  | { kind: 'checking' }
  | { kind: 'valid'; createdBy?: string | null; expiresAt?: string | null }
  | { kind: 'invalid' };

function readToken(hash: string): string | null {
  if (!hash.startsWith('#token=')) return null;
  return hash.slice('#token='.length);
}

export function AcceptInvitePage() {
  const { refresh } = useAuth();
  const navigate = useNavigate();
  // The raw token lives in the URL fragment (#token=...) so it never reaches
  // server logs or Referer headers; the browser/router exposes it locally.
  const location = useLocation();
  const token = useMemo(() => readToken(location.hash), [location.hash]);
  const [inspect, setInspect] = useState<InspectState>({ kind: 'idle' });
  const [username, setUsername] = useState('');
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const support = passkeysSupported();

  const checkInvite = useCallback(async () => {
    if (!token) {
      setInspect({ kind: 'invalid' });
      return;
    }
    setInspect({ kind: 'checking' });
    try {
      const res = await postApiV1AuthInvitationsInspect({ token });
      const body = res.data as unknown as {
        valid?: boolean;
        createdBy?: string | null;
        expiresAt?: string | null;
      };
      setInspect(body.valid ? { kind: 'valid', createdBy: body.createdBy, expiresAt: body.expiresAt } : { kind: 'invalid' });
    } catch {
      setInspect({ kind: 'invalid' });
    }
  }, [token]);

  useEffect(() => {
    void checkInvite();
  }, [checkInvite]);

  const handleAccept = async () => {
    if (!token) return;
    setBusy(true);
    setError(null);
    try {
      const credential = await registerPasskey(name.trim() || undefined, token);
      await completeRegistration(credential, {
        inviteToken: token,
        username: username.trim(),
        displayName: username.trim(),
      });
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
              <PersonAddAlt1Outlined sx={{ fontSize: 36, color: 'primary.main' }} />
              <Box>
                <Typography variant="h5" component="h1">Accept invitation</Typography>
                <Typography variant="body2" color="text.secondary">
                  Create your Hyveman account with a passkey.
                </Typography>
              </Box>
            </Stack>

            {inspect.kind === 'checking' ? (
              <Alert severity="info">Checking invitation…</Alert>
            ) : null}
            {inspect.kind === 'invalid' ? (
              <Alert severity="error" data-testid="invite-invalid">
                This invitation link is missing, invalid, expired or already used. Ask the person who
                invited you for a fresh link.
              </Alert>
            ) : null}
            {inspect.kind === 'valid' ? (
              <Alert severity="success" data-testid="invite-valid">
                Invitation verified
                {inspect.createdBy ? ` — invited by ${inspect.createdBy}` : ''}.
                {inspect.expiresAt ? ` Expires ${new Date(inspect.expiresAt).toLocaleString()}.` : ''}
              </Alert>
            ) : null}

            {!support.ok ? (
              <Alert severity="warning">{support.reason}</Alert>
            ) : null}

            <TextField
              label="Username"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              helperText="Your account name in this console (2–64 chars: letters, digits, - _ .)."
              disabled={busy}
              fullWidth
              autoFocus
            />
            <TextField
              label="Passkey name (optional)"
              value={name}
              onChange={(e) => setName(e.target.value)}
              helperText="A friendly label, e.g. “Work laptop”."
              disabled={busy}
              fullWidth
            />

            {error ? (
              <Alert severity="error" data-testid="invite-error">
                {error}
              </Alert>
            ) : null}

            <Button
              variant="contained"
              size="large"
              disabled={busy || !support.ok || inspect.kind !== 'valid' || username.trim().length < 2}
              onClick={() => void handleAccept()}
              data-testid="accept-button"
            >
              {busy ? 'Waiting for passkey…' : 'Create account'}
            </Button>
          </Stack>
        </CardContent>
      </Card>
    </Box>
  );
}
