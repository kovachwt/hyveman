/**
 * User administration (docs/MULTI-USER.md §8): users table with lifecycle
 * actions (disable/enable/delete — the API guards self- and last-user
 * lockouts), invite-link creation (raw URL shown once, token in the URL
 * fragment), pending invitations with revoke, and per-user passkey removal.
 */
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
import AddLink from '@mui/icons-material/AddLink';
import Block from '@mui/icons-material/Block';
import CheckCircle from '@mui/icons-material/CheckCircle';
import Delete from '@mui/icons-material/Delete';
import Key from '@mui/icons-material/Key';
import LinkOff from '@mui/icons-material/LinkOff';
import Person from '@mui/icons-material/Person';
import {
  useGetApiV1Users,
  useGetApiV1UsersInvitations,
  usePostApiV1UsersInvitations,
  usePostApiV1UsersInvitationsIdRevoke,
  usePostApiV1UsersIdDisable,
  usePostApiV1UsersIdEnable,
  useDeleteApiV1UsersId,
  useDeleteApiV1UsersIdPasskeysPasskeyId,
  type UserDto,
  type InvitationDto,
  type InvitationCreatedDto,
} from '@/api';
import { resourcePrefixes } from '@/api/queryKeys';
import { DataTable, type Column } from '@/components/DataTable/DataTable';
import { LoadingState } from '@/components/LoadingState/LoadingState';
import { EmptyState } from '@/components/EmptyState/EmptyState';
import { ErrorState, apiErrorMessage } from '@/components/ErrorState/ErrorState';
import { PageHeader } from '@/components/PageHeader/PageHeader';
import { ConfirmDialog } from '@/components/ConfirmDialog/ConfirmDialog';
import { TimeDisplay } from '@/components/TimeDisplay/TimeDisplay';

type InviteDialogState = { open: boolean; busy: boolean; days: string; error: unknown; created: InvitationCreatedDto | null };

const initialInvite: InviteDialogState = { open: false, busy: false, days: '7', error: null, created: null };

export default function UsersPage() {
  const queryClient = useQueryClient();
  const users = useGetApiV1Users({ query: { select: (r) => r.data } });
  const invitations = useGetApiV1UsersInvitations({ query: { select: (r) => r.data } });

  const [invite, setInvite] = useState<InviteDialogState>(initialInvite);
  const [removing, setRemoving] = useState<{ userId: string; passkeyId: string; label: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [revoking, setRevoking] = useState<InvitationDto | null>(null);

  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: resourcePrefixes.users });
    void queryClient.invalidateQueries({ queryKey: resourcePrefixes.passkeys });
  };

  const disable = usePostApiV1UsersIdDisable({ mutation: { onSuccess: invalidate } });
  const enable = usePostApiV1UsersIdEnable({ mutation: { onSuccess: invalidate } });
  const remove = useDeleteApiV1UsersId({ mutation: { onSuccess: invalidate } });
  const removePasskey = useDeleteApiV1UsersIdPasskeysPasskeyId({ mutation: { onSuccess: invalidate } });
  const createInvite = usePostApiV1UsersInvitations({ mutation: {} });
  const revokeInvite = usePostApiV1UsersInvitationsIdRevoke({ mutation: { onSuccess: invalidate } });

  const doCreateInvite = async () => {
    const days = Number(invite.days);
    if (!Number.isFinite(days) || days < 1 / 1440 || days > 7) {
      setInvite((s) => ({ ...s, error: new Error('Lifetime must be between 5 minutes and 7 days.') }));
      return;
    }
    setInvite((s) => ({ ...s, busy: true, error: null }));
    try {
      const res = await createInvite.mutateAsync({ expiresInMinutes: Math.round(days * 24 * 60) } as never);
      setInvite((s) => ({ ...s, busy: false, created: res.data as unknown as InvitationCreatedDto }));
      invalidate();
    } catch (err) {
      setInvite((s) => ({ ...s, busy: false, error: err }));
    }
  };

  const doRemovePasskey = async () => {
    if (!removing) return;
    setBusy(true);
    setError(null);
    try {
      await removePasskey.mutateAsync({ id: removing.userId, passkeyId: removing.passkeyId, params: { confirm: true } });
      setRemoving(null);
      invalidate();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  const doRevoke = async () => {
    if (!revoking) return;
    setBusy(true);
    setError(null);
    try {
      await revokeInvite.mutateAsync({ id: revoking.id ?? '' });
      setRevoking(null);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  const userColumns: Column<UserDto>[] = [
    {
      id: 'name',
      label: 'User',
      always: true,
      render: (u) => (
        <Stack direction="row" spacing={1} alignItems="center">
          <Person fontSize="small" color="action" />
          <Box>
            <Typography variant="body2" sx={{ fontWeight: 600 }}>{u.name}</Typography>
            {u.displayName && u.displayName !== u.name ? (
              <Typography variant="caption" color="text.secondary">{u.displayName}</Typography>
            ) : null}
          </Box>
        </Stack>
      ),
    },
    {
      id: 'state',
      label: 'State',
      render: (u) => (u.disabled ? <Typography variant="body2" color="error">disabled</Typography>
        : <Typography variant="body2" color="success.main">enabled</Typography>),
    },
    { id: 'passkeys', label: 'Passkeys', render: (u) => u.passkeyCount ?? 0 },
    { id: 'lastActive', label: 'Last active', render: (u) => (u.lastActive ? <TimeDisplay time={u.lastActive} variant="full" /> : 'never') },
    { id: 'created', label: 'Created', render: (u) => <TimeDisplay time={u.created} variant="full" /> },
    {
      id: 'actions',
      label: '',
      align: 'right',
      render: (u) => (
        <Stack direction="row" spacing={0.5} justifyContent="flex-end">
          {u.disabled ? (
            <Tooltip title="Enable user">
              <IconButton size="small" color="success" aria-label={`Enable ${u.name}`}
                onClick={() => void enable.mutateAsync({ id: u.id ?? '' })}>
                <CheckCircle fontSize="small" />
              </IconButton>
            </Tooltip>
          ) : (
            <Tooltip title="Disable user (revokes sessions)">
              <IconButton size="small" color="warning" aria-label={`Disable ${u.name}`}
                onClick={() => void disable.mutateAsync({ id: u.id ?? '' })}>
                <Block fontSize="small" />
              </IconButton>
            </Tooltip>
          )}
          <Tooltip title="Remove passkey">
            <span>
              <IconButton size="small" color="error" aria-label={`Remove passkey of ${u.name}`}
                disabled={!u.passkeyCount}
                onClick={() => {
                  if (!u.passkeyCount) return;
                  // Ask which passkey: fetch detail lazily via the API and
                  // offer the first (most users have few); the confirm dialog
                  // below is the explicit destructive gate.
                  void removeFirstPasskey(u);
                }}>
                <Key fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>
          <Tooltip title="Delete user (cascades passkeys + sessions)">
            <IconButton size="small" color="error" aria-label={`Delete ${u.name}`}
              onClick={() => {
                setError(null);
                if (window.confirm(`Delete user "${u.name}"? Their passkeys and sessions are removed. This cannot be undone.`)) {
                  void remove.mutateAsync({ id: u.id ?? '', params: { confirm: true } }).catch((e) => setError(e));
                }
              }}>
              <Delete fontSize="small" />
            </IconButton>
          </Tooltip>
        </Stack>
      ),
    },
  ];

  const removeFirstPasskey = async (u: UserDto) => {
    setError(null);
    setBusy(true);
    try {
      const { getApiV1UsersId } = await import('@/api/generated/endpoints');
      const res = await getApiV1UsersId(u.id ?? '');
      const detail = res.data as unknown as { passkeys?: Array<{ id?: string; name?: string }> };
      const first = detail.passkeys?.[0];
      if (!first?.id) {
        setError(new Error('This user has no passkeys to remove.'));
        return;
      }
      setRemoving({ userId: u.id ?? '', passkeyId: first.id, label: first.name || '(unnamed)' });
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  const inviteColumns: Column<InvitationDto>[] = [
    { id: 'createdBy', label: 'Invited by', render: (i) => i.createdByDisplayName ?? i.createdBy ?? '—' },
    { id: 'created', label: 'Created', render: (i) => <TimeDisplay time={i.created} variant="full" /> },
    { id: 'expires', label: 'Expires', render: (i) => (i.expiresAt ? <TimeDisplay time={i.expiresAt} variant="full" /> : 'never') },
    {
      id: 'state',
      label: 'State',
      render: (i) => {
        if (i.consumedAt) return <Typography variant="body2" color="text.secondary">used</Typography>;
        if (i.revoked) return <Typography variant="body2" color="error">revoked</Typography>;
        return <Typography variant="body2" color="success.main">pending</Typography>;
      },
    },
    {
      id: 'actions',
      label: '',
      align: 'right',
      render: (i) => (
        <Tooltip title="Revoke invitation">
          <IconButton size="small" color="error" aria-label="Revoke invitation"
            disabled={Boolean(i.consumedAt) || Boolean(i.revoked)}
            onClick={() => setRevoking(i)}>
            <LinkOff fontSize="small" />
          </IconButton>
        </Tooltip>
      ),
    },
  ];

  return (
    <Box>
      <PageHeader
        title="Users"
        subtitle="Operators with equal permissions. Each user owns their own passkeys; invite links let new users create their own account."
        actions={
          <Button variant="contained" startIcon={<AddLink />} onClick={() => setInvite({ ...initialInvite, open: true })}>
            Invite user
          </Button>
        }
      />

      {error ? <ErrorState compact error={error} title={apiErrorMessage(error)} /> : null}

      {users.isPending ? <LoadingState label="Loading users…" /> : null}
      {users.isError && !users.data ? <ErrorState error={users.error} onRetry={() => void users.refetch()} /> : null}
      {users.data && users.data.length === 0 ? (
        <EmptyState title="No users" description="Create the first user via first-run setup, then invite others." />
      ) : null}
      {users.data && users.data.length > 0 ? (
        <DataTable columns={userColumns} rows={users.data} rowKey={(u) => u.id ?? ''} maxHeight={420} aria-label="Users" />
      ) : null}

      <Typography variant="overline" color="text.secondary" sx={{ px: 1.5, fontSize: 11, display: 'block', mt: 3 }}>
        Invitations
      </Typography>
      {invitations.isPending ? <LoadingState label="Loading invitations…" /> : null}
      {invitations.data && invitations.data.length === 0 ? (
        <EmptyState title="No invitations" description="Invite links are single-use and expire (default 7 days)." />
      ) : null}
      {invitations.data && invitations.data.length > 0 ? (
        <DataTable columns={inviteColumns} rows={invitations.data} rowKey={(i) => i.id ?? ''} maxHeight={280} aria-label="Invitations" />
      ) : null}

      {/* Invite creation dialog: the raw link is shown exactly once. */}
      <Dialog open={invite.open} onClose={invite.busy ? undefined : () => setInvite(initialInvite)} maxWidth="sm" fullWidth>
        <DialogTitle>Invite a new user</DialogTitle>
        <DialogContent>
          {invite.created ? (
            <Stack spacing={1.5} sx={{ mt: 1 }}>
              <Alert severity="info">
                Share this link with the new user. It works once and expires
                {invite.created.expiresAt ? ` ${new Date(invite.created.expiresAt).toLocaleString()}` : ''}.
                Anyone with the link can create an account.
              </Alert>
              <TextField
                label="Invite link"
                value={invite.created.url ?? ''}
                fullWidth
                multiline
                slotProps={{ input: { readOnly: true } }}
              />
              <Typography variant="caption" color="text.secondary">
                The token lives in the URL fragment (#token=...) and is never sent to the server except
                during registration. Copy it to your clipboard now — it is shown only once.
              </Typography>
            </Stack>
          ) : (
            <Stack spacing={2} sx={{ mt: 1 }}>
              <TextField
                label="Lifetime (days, max 7)"
                value={invite.days}
                onChange={(e) => setInvite((s) => ({ ...s, days: e.target.value }))}
                disabled={invite.busy}
                fullWidth
                type="number"
                inputProps={{ min: 1 / 1440, max: 7, step: 0.25 }}
              />
              {invite.error ? <Alert severity="error">{apiErrorMessage(invite.error)}</Alert> : null}
            </Stack>
          )}
        </DialogContent>
        <DialogActions>
          {invite.created ? (
            <Button onClick={() => setInvite(initialInvite)} color="inherit">Close</Button>
          ) : (
            <>
              <Button onClick={() => setInvite(initialInvite)} disabled={invite.busy} color="inherit">Cancel</Button>
              <Button variant="contained" disabled={invite.busy} onClick={() => void doCreateInvite()}>
                {invite.busy ? 'Creating…' : 'Create invite'}
              </Button>
            </>
          )}
        </DialogActions>
      </Dialog>

      {/* Passkey removal confirm */}
      <ConfirmDialog
        open={removing !== null}
        title={`Remove passkey "${removing?.label}"?`}
        body="The passkey will no longer sign in to this account. The API prevents removing the last passkey of the last enabled user."
        confirmLabel="Remove passkey"
        danger
        busy={busy}
        onConfirm={() => void doRemovePasskey()}
        onCancel={() => { if (!busy) { setRemoving(null); setError(null); } }}
      />

      {/* Invitation revoke confirm */}
      <ConfirmDialog
        open={revoking !== null}
        title="Revoke invitation?"
        body="The invite link stops working immediately; the invitee cannot create an account with it."
        confirmLabel="Revoke invitation"
        danger
        busy={busy}
        onConfirm={() => void doRevoke()}
        onCancel={() => { if (!busy) { setRevoking(null); setError(null); } }}
      />
    </Box>
  );
}
