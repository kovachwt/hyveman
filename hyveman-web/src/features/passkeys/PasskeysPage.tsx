/** Passkey management (FRONTEND.md §7.4, docs/MULTI-USER.md): list the
 *  session user's own keys (the API scopes this server-side), register
 *  additional keys with the same ceremony, remove with confirmation — the
 *  API prevents removing the final usable passkey of the last enabled user. */
import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  IconButton,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import Add from '@mui/icons-material/Add';
import Delete from '@mui/icons-material/Delete';
import Key from '@mui/icons-material/Key';
import {
  deleteApiV1AuthPasskeysId,
  useGetApiV1AuthPasskeys,
} from '@/api';import { resourcePrefixes } from '@/api/queryKeys';
import { DataTable, type Column } from '@/components/DataTable/DataTable';
import { LoadingState } from '@/components/LoadingState/LoadingState';
import { EmptyState } from '@/components/EmptyState/EmptyState';
import { ErrorState, apiErrorMessage } from '@/components/ErrorState/ErrorState';
import { PageHeader } from '@/components/PageHeader/PageHeader';
import { ConfirmDialog } from '@/components/ConfirmDialog/ConfirmDialog';
import { TimeDisplay } from '@/components/TimeDisplay/TimeDisplay';
import { registerPasskey, completeRegistration, PasskeyError, passkeysSupported } from '@/auth/passkey';
import type { PasskeyDto } from '@/api/generated/endpoints';

export default function PasskeysPage() {
  const queryClient = useQueryClient();
  const passkeys = useGetApiV1AuthPasskeys({ query: { select: (r) => r.data } });

  const [addOpen, setAddOpen] = useState(false);
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [removing, setRemoving] = useState<PasskeyDto | null>(null);

  const support = passkeysSupported();

  const invalidate = () => void queryClient.invalidateQueries({ queryKey: resourcePrefixes.passkeys });

  const addPasskey = async () => {
    setBusy(true);
    setError(null);
    try {
      const credential = await registerPasskey(name.trim() || undefined);
      await completeRegistration(credential);
      invalidate();
      setAddOpen(false);
      setName('');
    } catch (err) {
      setError(err instanceof PasskeyError ? err.message : 'Registration failed. Please try again.');
    } finally {
      setBusy(false);
    }
  };

  const removePasskey = async () => {
    if (!removing) return;
    setBusy(true);
    setError(null);
    try {
      await deleteApiV1AuthPasskeysId(removing.id ?? '', { confirm: true });
      invalidate();
      setRemoving(null);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<PasskeyDto>[] = [
    {
      id: 'name',
      label: 'Name',
      always: true,
      render: (p) => (
        <Stack direction="row" spacing={1} alignItems="center">
          <Key fontSize="small" color="action" />
          <Typography variant="body2" sx={{ fontWeight: 600 }}>{p.name || '(unnamed)'}</Typography>
        </Stack>
      ),
    },
    { id: 'created', label: 'Created', render: (p) => <TimeDisplay time={p.created} variant="full" /> },
    { id: 'used', label: 'Last used', render: (p) => (p.lastUsed ? <TimeDisplay time={p.lastUsed} variant="full" /> : 'never') },
    {
      id: 'actions',
      label: '',
      align: 'right',
      render: (p) => (
        <Tooltip title="Remove passkey">
          <IconButton size="small" color="error" aria-label={`Remove passkey ${p.name || p.id}`} onClick={() => setRemoving(p)}>
            <Delete fontSize="small" />
          </IconButton>
        </Tooltip>
      ),
    },
  ];

  return (
    <Box>
      <PageHeader
        title="My passkeys"
        subtitle="WebAuthn credentials for your account. You can add several (e.g. phone + laptop); the API blocks removing your last passkey."
        actions={
          <Button variant="contained" startIcon={<Add />} disabled={!support.ok} onClick={() => { setAddOpen(true); setError(null); }}>
            Add passkey
          </Button>
        }
      />

      {!support.ok ? (
        <Alert severity="warning" sx={{ mb: 2 }}>{support.reason}</Alert>
      ) : null}

      {error ? <ErrorState compact error={error} title={apiErrorMessage(error)} /> : null}

      {passkeys.isPending ? <LoadingState label="Loading passkeys…" /> : null}
      {passkeys.isError && !passkeys.data ? <ErrorState error={passkeys.error} onRetry={() => void passkeys.refetch()} /> : null}
      {passkeys.data && passkeys.data.length === 0 ? (
        <EmptyState title="No passkeys" description="Register the first passkey to enable sign-in for this console." />
      ) : null}
      {passkeys.data && passkeys.data.length > 0 ? (
        <DataTable columns={columns} rows={passkeys.data} rowKey={(p) => p.id ?? ''} maxHeight={480} aria-label="Passkeys" getRowProps={() => ({ style: { cursor: 'default' } })} />
      ) : null}

      <Dialog open={addOpen} onClose={busy ? undefined : () => setAddOpen(false)} maxWidth="xs" fullWidth>
        <DialogTitle>Add passkey</DialogTitle>
        <DialogContent>
          <TextField
            label="Passkey name (optional)"
            fullWidth
            margin="dense"
            value={name}
            onChange={(e) => setName(e.target.value)}
            disabled={busy}
            autoFocus
          />
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
            Your browser will prompt for the platform or security-key authenticator.
          </Typography>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setAddOpen(false)} disabled={busy} color="inherit">Cancel</Button>
          <Button variant="contained" disabled={busy} onClick={() => void addPasskey()}>
            {busy ? 'Waiting for passkey…' : 'Register'}
          </Button>
        </DialogActions>
      </Dialog>

      <ConfirmDialog
        open={removing !== null}
        title={`Remove passkey "${removing?.name || (removing?.id ?? '')}"?`}
        body="You will not be able to sign in with this passkey afterwards. The API prevents removing the final usable passkey."
        confirmLabel="Remove passkey"
        danger
        busy={busy}
        onConfirm={() => void removePasskey()}
        onCancel={() => { if (!busy) { setRemoving(null); setError(null); } }}
      />
    </Box>
  );
}
