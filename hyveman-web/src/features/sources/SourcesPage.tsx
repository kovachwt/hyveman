/** Sources & registration tokens (FRONTEND.md §8.6): agent sources, token
 *  lifecycle, and one-time registration tokens — the raw token is displayed
 *  exactly once with a copy button, then dropped from component state. */
import { useMemo, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useForm, Controller } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import {
  Alert,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  IconButton,
  MenuItem,
  Paper,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import Add from '@mui/icons-material/Add';
import ContentCopy from '@mui/icons-material/ContentCopy';
import Delete from '@mui/icons-material/Delete';
import WarningAmber from '@mui/icons-material/WarningAmber';
import {
  postApiV1RegistrationTokens,
  postApiV1RegistrationTokensIdRevoke,
  postApiV1SourcesSourceIdTokensTokenIdRevoke,
  useGetApiV1RegistrationTokens,
  useGetApiV1Sources,
} from '@/api';
import { resourcePrefixes } from '@/api/queryKeys';
import { DataTable, type Column } from '@/components/DataTable/DataTable';
import { LoadingState } from '@/components/LoadingState/LoadingState';
import { EmptyState } from '@/components/EmptyState/EmptyState';
import { ErrorState, apiErrorMessage } from '@/components/ErrorState/ErrorState';
import { PageHeader } from '@/components/PageHeader/PageHeader';
import { ConfirmDialog } from '@/components/ConfirmDialog/ConfirmDialog';
import { HealthBadge } from '@/components/HealthBadge/HealthBadge';
import { TimeDisplay } from '@/components/TimeDisplay/TimeDisplay';
import type { SourceDto, TokenDto, RegistrationTokenDto } from '@/api/generated/endpoints';

const tokenSchema = z.object({
  kind: z.string().min(1, 'Kind is required.'),
  lifetimeMinutes: z
    .string()
    .refine((v) => v === '' || (Number.isInteger(Number(v)) && Number(v) > 0), 'Lifetime must be a positive number of minutes.'),
});
type TokenForm = z.infer<typeof tokenSchema>;

const SOURCE_KINDS = ['windows-agent', 'linux-agent', 'syslog-feed'];

export default function SourcesPage() {
  const queryClient = useQueryClient();
  const sources = useGetApiV1Sources({ query: { select: (r) => r.data } });
  const tokens = useGetApiV1RegistrationTokens({ query: { select: (r) => r.data } });

  const [createOpen, setCreateOpen] = useState(false);
  const [createdToken, setCreatedToken] = useState<string | null>(null);
  const [revokingToken, setRevokingToken] = useState<{ sourceId?: string; tokenId: string; label: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [copied, setCopied] = useState(false);

  const { control, handleSubmit, reset } = useForm<TokenForm>({
    resolver: zodResolver(tokenSchema),
    defaultValues: { kind: 'windows-agent', lifetimeMinutes: '' },
  });

  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: resourcePrefixes.sources });
    void queryClient.invalidateQueries({ queryKey: resourcePrefixes.registrationTokens });
  };

  const submitToken = async (values: TokenForm) => {
    setBusy(true);
    setError(null);
    try {
      const res = await postApiV1RegistrationTokens({
        kind: values.kind,
        lifetimeMinutes: values.lifetimeMinutes === '' ? null : Number(values.lifetimeMinutes),
      });
      // The raw token exists exactly once in this response.
      setCreatedToken(res.data.token ?? null);
      invalidate();
      setCreateOpen(false);
      reset();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  const doRevoke = async () => {
    if (!revokingToken) return;
    setBusy(true);
    setError(null);
    try {
      if (revokingToken.sourceId) {
        await postApiV1SourcesSourceIdTokensTokenIdRevoke(revokingToken.sourceId, revokingToken.tokenId);
      } else {
        await postApiV1RegistrationTokensIdRevoke(revokingToken.tokenId);
      }
      invalidate();
      setRevokingToken(null);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  const copyToken = async () => {
    if (!createdToken) return;
    try {
      await navigator.clipboard.writeText(createdToken);
      setCopied(true);
    } catch {
      // Clipboard unavailable (permissions); the token stays visible for manual copy.
    }
  };

  const tokenColumns: Column<TokenDto>[] = [
    { id: 'prefix', label: 'Token', always: true, render: (t) => <Typography variant="body2" sx={{ fontFamily: 'monospace' }}>{t.prefix}…</Typography> },
    { id: 'scopes', label: 'Scopes', render: (t) => (t.scopes ?? []).join(', ') },
    { id: 'created', label: 'Created', render: (t) => <TimeDisplay time={t.created} variant="full" /> },
    { id: 'used', label: 'Last used', render: (t) => (t.lastUsed ? <TimeDisplay time={t.lastUsed} variant="full" /> : 'never') },
    { id: 'state', label: 'State', render: (t) => (t.revoked ? <Chip label="revoked" size="small" color="error" variant="outlined" /> : <Chip label="active" size="small" color="success" variant="outlined" />) },
    {
      id: 'actions',
      label: '',
      align: 'right',
      render: (t) =>
        t.revoked ? null : (
          <Tooltip title="Revoke agent token">
            <IconButton size="small" color="error" aria-label={`Revoke token ${t.prefix ?? ''}`} onClick={() => setRevokingToken({ tokenId: t.id ?? '', label: t.prefix ?? '' })}>
              <Delete fontSize="small" />
            </IconButton>
          </Tooltip>
        ),
    },
  ];

  const regTokenColumns: Column<RegistrationTokenDto>[] = [
    { id: 'kind', label: 'Kind', always: true, render: (t) => t.kind },
    { id: 'created', label: 'Created', render: (t) => <TimeDisplay time={t.created} variant="full" /> },
    { id: 'expires', label: 'Expires', render: (t) => (t.expiresAt ? <TimeDisplay time={t.expiresAt} variant="full" /> : 'never') },
    { id: 'consumed', label: 'Consumed', render: (t) => (t.consumedAt ? <TimeDisplay time={t.consumedAt} variant="full" /> : '—') },
    { id: 'state', label: 'State', render: (t) => (t.revoked ? <Chip label="revoked" size="small" color="error" variant="outlined" /> : t.consumedAt ? <Chip label="used" size="small" color="info" variant="outlined" /> : <Chip label="pending" size="small" color="success" variant="outlined" />) },
    {
      id: 'actions',
      label: '',
      align: 'right',
      render: (t) =>
        t.revoked || t.consumedAt ? null : (
          <Tooltip title="Revoke registration token">
            <IconButton size="small" color="error" aria-label={`Revoke registration token for ${t.kind ?? ''}`} onClick={() => setRevokingToken({ tokenId: t.id ?? '', label: `${t.kind ?? ''} token` })}>
              <Delete fontSize="small" />
            </IconButton>
          </Tooltip>
        ),
    },
  ];

  const sourceRows = useMemo(() => sources.data ?? [], [sources.data]);

  return (
    <Box>
      <PageHeader
        title="Sources & tokens"
        subtitle="Agent sources, agent bearer tokens, and one-time registration tokens."
      />

      {error ? <ErrorState compact error={error} title={apiErrorMessage(error)} /> : null}

      <Typography variant="overline" color="text.secondary">Agent sources</Typography>
      {sources.isPending ? <LoadingState label="Loading sources…" /> : null}
      {sources.isError && !sources.data ? <ErrorState error={sources.error} onRetry={() => void sources.refetch()} /> : null}
      {sources.data && sourceRows.length === 0 ? (
        <EmptyState
          title="No sources registered"
          description="Agents enroll themselves with a registration token. Create one below and run the agent enrollment with it."
        />
      ) : null}

      {sourceRows.map((source: SourceDto) => (
        <Paper variant="outlined" sx={{ p: 2, mb: 2 }} key={source.id}>
          <Stack direction={{ xs: 'column', md: 'row' }} spacing={1} sx={{ alignItems: { md: 'center' }, mb: 1 }}>
            <Typography variant="h6" component="h3">{source.name}</Typography>
            <Chip label={source.kind} size="small" variant="outlined" />
            <HealthBadge state={source.agent?.status} size="small" label={source.agent?.status ?? 'no agent data'} />
            <Typography variant="caption" color="text.secondary" sx={{ ml: { md: 'auto' } }}>
              {source.id}
              {source.agent?.lastReceived ? <> · heartbeat <TimeDisplay time={source.agent.lastReceived} /></> : null}
            </Typography>
          </Stack>
          {(source.tokens ?? []).length > 0 ? (
            <DataTable columns={tokenColumns} rows={source.tokens ?? []} rowKey={(t) => t.id ?? ''} maxHeight={260} aria-label={`Tokens for ${source.name ?? ''}`} getRowProps={() => ({ style: { cursor: 'default' } })} />
          ) : (
            <Typography variant="body2" color="text.secondary">No agent tokens.</Typography>
          )}
        </Paper>
      ))}

      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mt: 3, mb: 1 }}>
        <Typography variant="overline" color="text.secondary">Registration tokens</Typography>
        <Button variant="contained" size="small" startIcon={<Add />} onClick={() => { setCreateOpen(true); setError(null); }}>
          Create token
        </Button>
      </Stack>

      {tokens.isPending ? <LoadingState label="Loading registration tokens…" /> : null}
      {tokens.isError && !tokens.data ? <ErrorState error={tokens.error} onRetry={() => void tokens.refetch()} /> : null}
      {tokens.data && tokens.data.length === 0 ? (
        <Typography variant="body2" color="text.secondary">No registration tokens yet.</Typography>
      ) : null}
      {tokens.data && tokens.data.length > 0 ? (
        <DataTable columns={regTokenColumns} rows={tokens.data} rowKey={(t) => t.id ?? ''} maxHeight={280} aria-label="Registration tokens" getRowProps={() => ({ style: { cursor: 'default' } })} />
      ) : null}

      {/* Create token dialog */}
      <Dialog open={createOpen} onClose={busy ? undefined : () => setCreateOpen(false)} maxWidth="xs" fullWidth>
        <DialogTitle>Create registration token</DialogTitle>
        <form onSubmit={handleSubmit(submitToken)} noValidate>
          <DialogContent>
            <Controller
              name="kind"
              control={control}
              render={({ field, fieldState }) => (
                <TextField {...field} select label="Source kind *" fullWidth margin="dense" error={Boolean(fieldState.error)} helperText={fieldState.error?.message} disabled={busy}>
                  {SOURCE_KINDS.map((k) => (
                    <MenuItem key={k} value={k}>{k}</MenuItem>
                  ))}
                </TextField>
              )}
            />
            <Controller
              name="lifetimeMinutes"
              control={control}
              render={({ field, fieldState }) => (
                <TextField
                  {...field}
                  label="Lifetime (minutes, optional)"
                  type="number"
                  fullWidth
                  margin="dense"
                  error={Boolean(fieldState.error)}
                  helperText={fieldState.error?.message ?? 'Blank = no expiry (server policy may still cap it).'}
                  disabled={busy}
                />
              )}
            />
          </DialogContent>
          <DialogActions>
            <Button onClick={() => setCreateOpen(false)} disabled={busy} color="inherit">Cancel</Button>
            <Button type="submit" variant="contained" disabled={busy}>{busy ? 'Creating…' : 'Create token'}</Button>
          </DialogActions>
        </form>
      </Dialog>

      {/* Raw token: shown exactly once, cleared when dismissed; never in the
          URL, analytics, or web storage. */}
      <Dialog open={createdToken !== null} onClose={() => setCreatedToken(null)} maxWidth="sm" fullWidth>
        <DialogTitle>Registration token (shown once)</DialogTitle>
        <DialogContent>
          <Alert severity="warning" icon={<WarningAmber />} sx={{ mb: 2 }}>
            Copy this token now. It will never be shown again and is not retrievable. Keep it out of
            URLs, logs, and chat. It expires after its lifetime or when consumed.
          </Alert>
          <Paper variant="outlined" sx={{ p: 1.5, display: 'flex', alignItems: 'center', gap: 1, bgcolor: 'action.hover' }}>
            <Typography variant="body2" sx={{ fontFamily: 'monospace', wordBreak: 'break-all', flexGrow: 1 }} data-testid="raw-token">
              {createdToken}
            </Typography>
            <Tooltip title={copied ? 'Copied' : 'Copy token'}>
              <Button size="small" startIcon={<ContentCopy />} onClick={() => void copyToken()} data-testid="copy-token">
                {copied ? 'Copied' : 'Copy'}
              </Button>
            </Tooltip>
          </Paper>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => { setCreatedToken(null); setCopied(false); }}>Close</Button>
        </DialogActions>
      </Dialog>

      <ConfirmDialog
        open={revokingToken !== null}
        title={`Revoke ${revokingToken?.label ?? 'token'}?`}
        body="The token stops working immediately. An agent using it will be rejected on its next request."
        confirmLabel="Revoke"
        danger
        busy={busy}
        onConfirm={() => void doRevoke()}
        onCancel={() => { if (!busy) { setRevokingToken(null); setError(null); } }}
      />
    </Box>
  );
}
