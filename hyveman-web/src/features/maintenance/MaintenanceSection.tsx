/**
 * Maintenance windows (FRONTEND.md §8.4/§8.6): CRUD with explicit confirm on
 * delete. Used host-scoped (HostDetailPage tab) and global (MaintenancePage).
 */
import { useMemo, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import {
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  IconButton,
  MenuItem,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import Add from '@mui/icons-material/Add';
import Delete from '@mui/icons-material/Delete';
import { useForm, Controller } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import {
  deleteApiV1MaintenanceWindowsId,
  postApiV1MaintenanceWindows,
  useGetApiV1MaintenanceWindows,
  useGetApiV1Hosts,
} from '@/api';
import { resourcePrefixes } from '@/api/queryKeys';
import { DataTable, type Column } from '@/components/DataTable/DataTable';
import { LoadingState } from '@/components/LoadingState/LoadingState';
import { EmptyState } from '@/components/EmptyState/EmptyState';
import { ErrorState, apiErrorMessage } from '@/components/ErrorState/ErrorState';
import { ConfirmDialog } from '@/components/ConfirmDialog/ConfirmDialog';
import { TimeDisplay } from '@/components/TimeDisplay/TimeDisplay';
import type { MaintenanceWindowDto } from '@/api/generated/endpoints';

const windowSchema = z
  .object({
    hostId: z.string(),
    start: z.string().min(1, 'Start time is required.'),
    end: z.string().min(1, 'End time is required.'),
    reason: z.string().trim().max(500, 'Reason is too long.'),
  })
  .superRefine((v, ctx) => {
    const start = new Date(v.start);
    const end = new Date(v.end);
    if (Number.isNaN(start.getTime())) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ['start'], message: 'Invalid start time.' });
      return;
    }
    if (Number.isNaN(end.getTime())) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ['end'], message: 'Invalid end time.' });
      return;
    }
    if (end <= start) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ['end'], message: 'End must be after start.' });
    }
  });

type WindowForm = z.infer<typeof windowSchema>;

export function MaintenanceSection({
  hostId,
  hostName,
}: {
  hostId?: string;
  hostName?: string;
}) {
  const queryClient = useQueryClient();
  const windows = useGetApiV1MaintenanceWindows({ query: { select: (r) => r.data } });
  const hosts = useGetApiV1Hosts({ query: { select: (r) => r.data } });

  const [createOpen, setCreateOpen] = useState(false);
  const [deleting, setDeleting] = useState<MaintenanceWindowDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const { control, handleSubmit, reset } = useForm<WindowForm>({
    resolver: zodResolver(windowSchema),
    defaultValues: { hostId: hostId ?? '', start: '', end: '', reason: '' },
  });

  const rows = useMemo(() => {
    const all = windows.data ?? [];
    const list = hostId ? all.filter((w) => w.hostId === hostId) : all;
    return [...list].sort((a, b) => new Date(b.start ?? 0).getTime() - new Date(a.start ?? 0).getTime());
  }, [windows.data, hostId]);

  const now = Date.now();
  const active = rows.filter((w) => new Date(w.start ?? 0).getTime() <= now && new Date(w.end ?? 0).getTime() > now);
  const upcoming = rows.filter((w) => new Date(w.start ?? 0).getTime() > now);
  const past = rows.filter((w) => new Date(w.end ?? 0).getTime() <= now);

  const invalidate = () => void queryClient.invalidateQueries({ queryKey: resourcePrefixes.maintenanceWindows });

  const submit = async (values: WindowForm) => {
    setBusy(true);
    setError(null);
    try {
      await postApiV1MaintenanceWindows({
        hostId: hostId || values.hostId || null,
        start: new Date(values.start).toISOString(),
        end: new Date(values.end).toISOString(),
        reason: values.reason || null,
      });
      invalidate();
      setCreateOpen(false);
      reset();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  const doDelete = async () => {
    if (!deleting) return;
    setBusy(true);
    setError(null);
    try {
      await deleteApiV1MaintenanceWindowsId(deleting.id ?? '');
      invalidate();
      setDeleting(null);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<MaintenanceWindowDto>[] = [
    {
      id: 'host',
      label: 'Host',
      always: true,
      render: (w) =>
        w.hostId ? (
          hosts.data?.find((h) => h.id === w.hostId)?.name ?? w.hostId
        ) : (
          <Chip label="All hosts" size="small" variant="outlined" />
        ),
    },
    { id: 'start', label: 'Start', render: (w) => <TimeDisplay time={w.start} variant="full" /> },
    { id: 'end', label: 'End', render: (w) => <TimeDisplay time={w.end} variant="full" /> },
    { id: 'reason', label: 'Reason', render: (w) => <Typography variant="body2" sx={{ whiteSpace: 'normal' }}>{w.reason ?? '—'}</Typography> },
    {
      id: 'actions',
      label: '',
      align: 'right',
      render: (w) => (
        <Tooltip title="Delete window">
          <IconButton size="small" aria-label={`Delete maintenance window for ${w.hostId ?? 'all hosts'}`} color="error" onClick={() => setDeleting(w)}>
            <Delete fontSize="small" />
          </IconButton>
        </Tooltip>
      ),
    },
  ];

  const renderList = (title: string, items: MaintenanceWindowDto[]) =>
    items.length > 0 ? (
      <Box sx={{ mb: 3 }}>
        <Typography variant="overline" color="text.secondary">{title}</Typography>
        <DataTable columns={columns} rows={items} rowKey={(w) => w.id ?? ''} maxHeight={320} aria-label={title} getRowProps={() => ({ style: { cursor: 'default' } })} />
      </Box>
    ) : null;

  return (
    <Box>
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 1 }}>
        <Typography variant="overline" color="text.secondary">
          Maintenance windows {hostName ? `for ${hostName}` : ''}
        </Typography>
        <Button
          size="small"
          variant="outlined"
          startIcon={<Add />}
          onClick={() => { reset({ hostId: hostId ?? '', start: '', end: '', reason: '' }); setCreateOpen(true); }}
        >
          New window
        </Button>
      </Stack>

      {error ? <ErrorState compact error={error} title={apiErrorMessage(error)} /> : null}

      {windows.isPending ? <LoadingState label="Loading maintenance windows…" /> : null}
      {windows.isError && !windows.data ? <ErrorState error={windows.error} onRetry={() => void windows.refetch()} /> : null}

      {windows.data && rows.length === 0 ? (
        <EmptyState title="No maintenance windows" description="Create a window to suppress alerts during planned work." />
      ) : (
        <>
          {renderList('Active', active)}
          {renderList('Upcoming', upcoming)}
          {renderList('Past', past)}
        </>
      )}

      <Dialog open={createOpen} onClose={busy ? undefined : () => setCreateOpen(false)} maxWidth="sm" fullWidth>
        <DialogTitle>New maintenance window</DialogTitle>
        <form onSubmit={handleSubmit(submit)} noValidate>
          <DialogContent>
            {!hostId ? (
              <Controller
                name="hostId"
                control={control}
                render={({ field }) => (
                  <TextField {...field} select label="Host" fullWidth margin="dense" disabled={busy}>
                    <MenuItem value="">All hosts</MenuItem>
                    {(hosts.data ?? []).map((h) => (
                      <MenuItem key={h.id} value={h.id}>{h.name}</MenuItem>
                    ))}
                  </TextField>
                )}
              />
            ) : null}
            <Controller
              name="start"
              control={control}
              render={({ field, fieldState }) => (
                <TextField
                  {...field}
                  label="Start (local time)"
                  type="datetime-local"
                  fullWidth
                  margin="dense"
                  InputLabelProps={{ shrink: true }}
                  error={Boolean(fieldState.error)}
                  helperText={fieldState.error?.message}
                  disabled={busy}
                />
              )}
            />
            <Controller
              name="end"
              control={control}
              render={({ field, fieldState }) => (
                <TextField
                  {...field}
                  label="End (local time)"
                  type="datetime-local"
                  fullWidth
                  margin="dense"
                  InputLabelProps={{ shrink: true }}
                  error={Boolean(fieldState.error)}
                  helperText={fieldState.error?.message}
                  disabled={busy}
                />
              )}
            />
            <Controller
              name="reason"
              control={control}
              render={({ field, fieldState }) => (
                <TextField
                  {...field}
                  label="Reason (optional)"
                  fullWidth
                  margin="dense"
                  multiline
                  minRows={2}
                  error={Boolean(fieldState.error)}
                  helperText={fieldState.error?.message}
                  disabled={busy}
                />
              )}
            />
          </DialogContent>
          <DialogActions>
            <Button onClick={() => setCreateOpen(false)} disabled={busy} color="inherit">Cancel</Button>
            <Button type="submit" variant="contained" disabled={busy}>{busy ? 'Creating…' : 'Create window'}</Button>
          </DialogActions>
        </form>
      </Dialog>

      <ConfirmDialog
        open={deleting !== null}
        title="Delete maintenance window?"
        body="Alerts will no longer be suppressed for this window. This cannot be undone."
        confirmLabel="Delete"
        danger
        busy={busy}
        onConfirm={() => void doDelete()}
        onCancel={() => { if (!busy) { setDeleting(null); setError(null); } }}
      />
    </Box>
  );
}
