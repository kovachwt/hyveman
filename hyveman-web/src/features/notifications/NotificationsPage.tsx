/** Notification channel CRUD + test (FRONTEND.md §8.5): secrets are
 *  write-only; test results never expose provider response bodies. */
import { useEffect, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useForm, Controller } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import {
  Alert,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  IconButton,
  MenuItem,
  Stack,
  Switch,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import Add from '@mui/icons-material/Add';
import Delete from '@mui/icons-material/Delete';
import Edit from '@mui/icons-material/Edit';
import Send from '@mui/icons-material/Send';
import {
  deleteApiV1NotificationChannelsId,
  patchApiV1NotificationChannelsId,
  postApiV1NotificationChannelsIdTest,
  postApiV1NotificationChannels,
  useGetApiV1NotificationChannels,
} from '@/api';
import { resourcePrefixes } from '@/api/queryKeys';
import { DataTable, type Column } from '@/components/DataTable/DataTable';
import { LoadingState } from '@/components/LoadingState/LoadingState';
import { EmptyState } from '@/components/EmptyState/EmptyState';
import { ErrorState, apiErrorMessage } from '@/components/ErrorState/ErrorState';
import { PageHeader } from '@/components/PageHeader/PageHeader';
import { ConfirmDialog } from '@/components/ConfirmDialog/ConfirmDialog';
import { TimeDisplay } from '@/components/TimeDisplay/TimeDisplay';
import { SecretField } from '@/components/SecretField/SecretField';
import type { ChannelDto } from '@/api/generated/endpoints';
import {
  buildChannelInput,
  CHANNEL_KIND_LABELS,
  channelFormFromDto,
  channelFormSchema,
  emptyChannelForm,
  type ChannelFormValues,
} from './channelForm';

export default function NotificationsPage() {
  const queryClient = useQueryClient();
  const channels = useGetApiV1NotificationChannels({ query: { select: (r) => r.data } });

  const [formOpen, setFormOpen] = useState(false);
  const [editing, setEditing] = useState<ChannelDto | null>(null);
  const [deleting, setDeleting] = useState<ChannelDto | null>(null);
  const [testing, setTesting] = useState<ChannelDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [testResult, setTestResult] = useState<{ ok: boolean; at: string; error?: string } | null>(null);

  const { control, handleSubmit, reset, watch } = useForm<ChannelFormValues>({
    resolver: zodResolver(channelFormSchema(editing !== null)),
    defaultValues: emptyChannelForm(),
    mode: 'onTouched',
  });
  const kind = watch('kind');

  useEffect(() => {
    if (formOpen) reset(editing ? channelFormFromDto(editing) : emptyChannelForm());
  }, [formOpen, editing, reset]);

  const invalidate = () => void queryClient.invalidateQueries({ queryKey: resourcePrefixes.channels });

  const submit = async (values: ChannelFormValues) => {
    setBusy(true);
    setError(null);
    try {
      const input = buildChannelInput(values, editing !== null, editing?.updatedAt ?? undefined);
      if (editing) await patchApiV1NotificationChannelsId(editing.id ?? '', input);
      else await postApiV1NotificationChannels(input);
      invalidate();
      setFormOpen(false);
    } catch (err) {
      setError(err);
      // Secrets are not restored after an error; only non-secret values stay.
      reset({ ...values, telegramBotToken: '', telegramChatId: '', webhookUrl: '', smtpPassword: '' });
    } finally {
      setBusy(false);
    }
  };

  const doDelete = async () => {
    if (!deleting) return;
    setBusy(true);
    setError(null);
    try {
      await deleteApiV1NotificationChannelsId(deleting.id ?? '', { confirm: true });
      invalidate();
      setDeleting(null);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  const doTest = async () => {
    if (!testing) return;
    setBusy(true);
    setError(null);
    setTestResult(null);
    try {
      const res = await postApiV1NotificationChannelsIdTest(testing.id ?? '');
      const data = res.data;
      setTestResult({ ok: Boolean(data.ok), at: data.testedAt ?? '', error: data.error ?? undefined });
      invalidate();
      setTesting(null);
    } catch (err) {
      setError(err);
      setTesting(null);
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<ChannelDto>[] = [
    {
      id: 'name',
      label: 'Channel',
      always: true,
      render: (c) => (
        <Stack>
          <Typography variant="body2" sx={{ fontWeight: 600 }}>{c.name}</Typography>
          <Typography variant="caption" color="text.secondary">{c.configSummary && Object.keys(c.configSummary).length > 0 ? Object.entries(c.configSummary).map(([k, v]) => `${k}=${v}`).join(' · ') : ''}</Typography>
        </Stack>
      ),
    },
    { id: 'kind', label: 'Type', render: (c) => <Chip label={CHANNEL_KIND_LABELS[c.kind as keyof typeof CHANNEL_KIND_LABELS] ?? c.kind} size="small" variant="outlined" /> },
    { id: 'enabled', label: 'Enabled', render: (c) => (c.enabled ? 'Yes' : 'No') },
    { id: 'created', label: 'Created', render: (c) => <TimeDisplay time={c.created} variant="full" /> },
    { id: 'rotated', label: 'Secret rotated', render: (c) => (c.rotated ? <TimeDisplay time={c.rotated} variant="full" /> : '—') },
    {
      id: 'test',
      label: 'Last test',
      render: (c) =>
        c.lastTestAt ? (
          <Stack>
            <TimeDisplay time={c.lastTestAt} variant="full" />
            <Typography variant="caption" color={c.lastTestOk ? 'success.main' : 'error.main'}>
              {c.lastTestOk ? 'succeeded' : 'failed'}
            </Typography>
          </Stack>
        ) : (
          'never'
        ),
    },
    {
      id: 'actions',
      label: 'Actions',
      align: 'right',
      render: (c) => (
        <Stack direction="row" spacing={0.25} justifyContent="flex-end">
          <Tooltip title="Send test notification">
            <IconButton size="small" aria-label={`Test channel ${c.name}`} onClick={() => { setTestResult(null); setTesting(c); }}>
              <Send fontSize="small" />
            </IconButton>
          </Tooltip>
          <Tooltip title="Edit">
            <IconButton size="small" aria-label={`Edit channel ${c.name}`} onClick={() => { setEditing(c); setFormOpen(true); }}>
              <Edit fontSize="small" />
            </IconButton>
          </Tooltip>
          <Tooltip title="Delete">
            <IconButton size="small" color="error" aria-label={`Delete channel ${c.name}`} onClick={() => setDeleting(c)}>
              <Delete fontSize="small" />
            </IconButton>
          </Tooltip>
        </Stack>
      ),
    },
  ];

  return (
    <Box>
      <PageHeader
        title="Notification channels"
        subtitle="Telegram, webhook, and SMTP delivery. Secret values are write-only: they are never returned by the API or kept in the browser."
        actions={
          <Button variant="contained" startIcon={<Add />} onClick={() => { setEditing(null); setFormOpen(true); }}>
            New channel
          </Button>
        }
      />

      {channels.isPending ? <LoadingState label="Loading channels…" /> : null}
      {channels.isError && !channels.data ? <ErrorState error={channels.error} onRetry={() => void channels.refetch()} /> : null}
      {channels.data && channels.data.length === 0 ? (
        <EmptyState
          title="No channels yet"
          description="Create a channel so alert rules can notify you."
          action={<Button variant="contained" startIcon={<Add />} onClick={() => { setEditing(null); setFormOpen(true); }}>New channel</Button>}
        />
      ) : null}
      {channels.data && channels.data.length > 0 ? (
        <DataTable columns={columns} rows={channels.data} rowKey={(c) => c.id ?? ''} maxHeight={560} aria-label="Notification channels" getRowProps={() => ({ style: { cursor: 'default' } })} />
      ) : null}

      {testResult ? (
        <Alert severity={testResult.ok ? 'success' : 'error'} sx={{ mt: 2 }} data-testid="channel-test-result">
          {testResult.ok
            ? 'Test notification sent successfully.'
            : `Test notification failed${testResult.error ? `: ${testResult.error}` : ''}.`}
        </Alert>
      ) : null}

      <Dialog open={formOpen} onClose={busy ? undefined : () => setFormOpen(false)} maxWidth="sm" fullWidth>
        <DialogTitle>{editing ? `Edit channel: ${editing.name}` : 'New notification channel'}</DialogTitle>
        <form onSubmit={handleSubmit(submit)} noValidate>
          <DialogContent>
            {error ? <ErrorState compact error={error} title={apiErrorMessage(error)} /> : null}
            <Controller
              name="name"
              control={control}
              render={({ field, fieldState }) => (
                <TextField {...field} label="Name *" fullWidth margin="dense" error={Boolean(fieldState.error)} helperText={fieldState.error?.message} disabled={busy} />
              )}
            />
            <Controller
              name="kind"
              control={control}
              render={({ field, fieldState }) => (
                <TextField {...field} select label="Type *" fullWidth margin="dense" disabled={busy || editing !== null} error={Boolean(fieldState.error)} helperText={fieldState.error?.message}>
                  {(Object.keys(CHANNEL_KIND_LABELS) as (keyof typeof CHANNEL_KIND_LABELS)[]).map((k) => (
                    <MenuItem key={k} value={k}>{CHANNEL_KIND_LABELS[k]}</MenuItem>
                  ))}
                </TextField>
              )}
            />
            <Controller
              name="enabled"
              control={control}
              render={({ field }) => (
                <FormControlLabel
                  control={<Switch checked={field.value} onChange={(e) => field.onChange(e.target.checked)} disabled={busy} />}
                  label="Enabled"
                  sx={{ mt: 1 }}
                />
              )}
            />

            {kind === 'telegram' ? (
              <Stack spacing={0}>
                <Controller
                  name="telegramBotToken"
                  control={control}
                  render={({ field, fieldState }) => (
                    <SecretField {...field} label="Telegram bot token" fullWidth margin="dense" editMode={editing !== null} error={Boolean(fieldState.error)} helperText={fieldState.error?.message} disabled={busy} />
                  )}
                />
                <Controller
                  name="telegramChatId"
                  control={control}
                  render={({ field, fieldState }) => (
                    <SecretField {...field} label="Telegram chat ID" fullWidth margin="dense" editMode={editing !== null} error={Boolean(fieldState.error)} helperText={fieldState.error?.message} disabled={busy} />
                  )}
                />
              </Stack>
            ) : null}

            {kind === 'webhook' ? (
              <Controller
                name="webhookUrl"
                control={control}
                render={({ field, fieldState }) => (
                  <SecretField {...field} label="Webhook URL" fullWidth margin="dense" editMode={editing !== null} error={Boolean(fieldState.error)} helperText={fieldState.error?.message ?? 'https:// only.'} disabled={busy} />
                )}
              />
            ) : null}

            {kind === 'smtp' ? (
              <Stack spacing={0}>
                <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5}>
                  <Controller
                    name="smtpHost"
                    control={control}
                    render={({ field, fieldState }) => (
                      <TextField {...field} label="SMTP host" fullWidth margin="dense" error={Boolean(fieldState.error)} helperText={fieldState.error?.message} disabled={busy} />
                    )}
                  />
                  <Controller
                    name="smtpPort"
                    control={control}
                    render={({ field, fieldState }) => (
                      <TextField {...field} label="Port" fullWidth margin="dense" error={Boolean(fieldState.error)} helperText={fieldState.error?.message} disabled={busy} sx={{ minWidth: 110 }} />
                    )}
                  />
                </Stack>
                <Controller
                  name="smtpUsername"
                  control={control}
                  render={({ field, fieldState }) => (
                    <SecretField {...field} label="SMTP username" fullWidth margin="dense" editMode={editing !== null} error={Boolean(fieldState.error)} helperText={fieldState.error?.message} disabled={busy} />
                  )}
                />
                <Controller
                  name="smtpPassword"
                  control={control}
                  render={({ field, fieldState }) => (
                    <SecretField {...field} label="SMTP password" fullWidth margin="dense" editMode={editing !== null} error={Boolean(fieldState.error)} helperText={fieldState.error?.message} disabled={busy} />
                  )}
                />
                <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5}>
                  <Controller
                    name="smtpFrom"
                    control={control}
                    render={({ field, fieldState }) => (
                      <TextField {...field} label="From address" fullWidth margin="dense" error={Boolean(fieldState.error)} helperText={fieldState.error?.message} disabled={busy} />
                    )}
                  />
                  <Controller
                    name="smtpTo"
                    control={control}
                    render={({ field, fieldState }) => (
                      <TextField {...field} label="To address" fullWidth margin="dense" error={Boolean(fieldState.error)} helperText={fieldState.error?.message} disabled={busy} />
                    )}
                  />
                </Stack>
                <Controller
                  name="smtpUseTls"
                  control={control}
                  render={({ field }) => (
                    <FormControlLabel
                      control={<Switch checked={field.value} onChange={(e) => field.onChange(e.target.checked)} disabled={busy} />}
                      label="Use TLS"
                    />
                  )}
                />
              </Stack>
            ) : null}

            {editing ? (
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
                Blank secret fields keep the stored values. Secrets are sent only over HTTPS and are
                cleared after saving; the API never echoes them.
              </Typography>
            ) : null}
          </DialogContent>
          <DialogActions>
            <Button onClick={() => setFormOpen(false)} disabled={busy} color="inherit">Cancel</Button>
            <Button type="submit" variant="contained" disabled={busy}>{busy ? 'Saving…' : editing ? 'Save changes' : 'Create channel'}</Button>
          </DialogActions>
        </form>
      </Dialog>

      <ConfirmDialog
        open={deleting !== null}
        title={`Delete channel "${deleting?.name ?? ''}"?`}
        body="Notifications through this channel stop immediately. Rules referencing it keep their configuration."
        confirmLabel="Delete channel"
        danger
        busy={busy}
        onConfirm={() => void doDelete()}
        onCancel={() => { if (!busy) { setDeleting(null); setError(null); } }}
      />

      <ConfirmDialog
        open={testing !== null}
        title={`Send test notification via "${testing?.name ?? ''}"?`}
        body="A clearly labeled test message will be sent. The result is shown without exposing provider response details."
        confirmLabel="Send test"
        busy={busy}
        onConfirm={() => void doTest()}
        onCancel={() => { if (!busy) { setTesting(null); setError(null); } }}
      />
    </Box>
  );
}
