/** Alerts page (FRONTEND.md §8.4): active/acknowledged/silenced/history with
 *  explicit acknowledge/silence actions, confirmation with reason, and
 *  immediate query invalidation. Polls every 20 s while visible. */
import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import {
  Box,
  FormControl,
  IconButton,
  InputLabel,
  MenuItem,
  Paper,
  Select,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import CheckCircle from '@mui/icons-material/CheckCircle';
import NotificationsOff from '@mui/icons-material/NotificationsOff';
import Undo from '@mui/icons-material/Undo';
import VolumeOff from '@mui/icons-material/VolumeOff';
import {
  postApiV1AlertsIdAcknowledge,
  postApiV1AlertsIdSilence,
  postApiV1AlertsIdUnacknowledge,
  postApiV1AlertsIdUnsilence,
  useGetApiV1Alerts,
  useGetApiV1Hosts,
} from '@/api';
import { resourcePrefixes } from '@/api/queryKeys';
import { DataTable, type Column } from '@/components/DataTable/DataTable';
import { LoadingState } from '@/components/LoadingState/LoadingState';
import { EmptyState } from '@/components/EmptyState/EmptyState';
import { ErrorState } from '@/components/ErrorState/ErrorState';
import { PageHeader } from '@/components/PageHeader/PageHeader';
import { ConfirmDialog } from '@/components/ConfirmDialog/ConfirmDialog';
import { HealthBadge } from '@/components/HealthBadge/HealthBadge';
import { TimeDisplay } from '@/components/TimeDisplay/TimeDisplay';
import { formatCount } from '@/lib/format';
import { toLocalDateTimeInput } from '@/lib/format';
import type { AlertDto } from '@/api/generated/endpoints';
import {
  alertsFromSearchParams,
  alertsToApiParams,
  alertsToSearchParams,
  ALERT_STATUSES,
  buildAcknowledgeRequest,
  buildSilenceRequest,
} from './alertActions';

type Action =
  | { kind: 'ack'; alert: AlertDto }
  | { kind: 'silence'; alert: AlertDto }
  | { kind: 'unack'; alert: AlertDto }
  | { kind: 'unsilence'; alert: AlertDto };

export default function AlertsPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const queryClient = useQueryClient();

  const filters = useMemo(() => alertsFromSearchParams(searchParams), [searchParams]);
  const apiParams = alertsToApiParams(filters);

  const alerts = useGetApiV1Alerts(apiParams, {
    query: {
      select: (r) => r.data,
      refetchInterval: 20_000,
    },
  });
  const hosts = useGetApiV1Hosts({ query: { select: (r) => r.data } });

  const [action, setAction] = useState<Action | null>(null);
  const [silenceUntil, setSilenceUntil] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const invalidate = () => void queryClient.invalidateQueries({ queryKey: resourcePrefixes.alerts });

  const runAction = async (reason: string | undefined) => {
    if (!action) return;
    setBusy(true);
    setError(null);
    try {
      const id = action.alert.id ?? '';
      switch (action.kind) {
        case 'ack':
          await postApiV1AlertsIdAcknowledge(id, buildAcknowledgeRequest(reason));
          break;
        case 'silence':
          await postApiV1AlertsIdSilence(id, buildSilenceRequest(new Date(silenceUntil).toISOString(), reason));
          break;
        case 'unack':
          await postApiV1AlertsIdUnacknowledge(id);
          break;
        case 'unsilence':
          await postApiV1AlertsIdUnsilence(id);
          break;
      }
      invalidate();
      setAction(null);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  const openAction = (a: Action) => {
    setError(null);
    if (a.kind === 'silence') {
      const d = new Date(Date.now() + 2 * 3_600_000);
      setSilenceUntil(toLocalDateTimeInput(d.toISOString()));
    }
    setAction(a);
  };

  const columns: Column<AlertDto>[] = [
    {
      id: 'title',
      label: 'Alert',
      always: true,
      render: (a) => (
        <Stack>
          <Typography variant="body2" sx={{ fontWeight: 600 }}>{a.title}</Typography>
          <Typography variant="caption" color="text.secondary" sx={{ whiteSpace: 'normal' }}>{a.detail ?? ''}</Typography>
        </Stack>
      ),
    },
    { id: 'rule', label: 'Rule', render: (a) => a.ruleName ?? '—' },
    { id: 'host', label: 'Host', render: (a) => a.hostName ?? a.hostId ?? '—' },
    { id: 'severity', label: 'Severity', render: (a) => <HealthBadge state={a.severity} size="small" label={a.severity} /> },
    {
      id: 'status',
      label: 'Status',
      render: (a) => (
        <Typography variant="body2">
          {a.status}
          {a.ackAt ? <Typography variant="caption" color="text.secondary" component="div">acked <TimeDisplay time={a.ackAt} /></Typography> : null}
          {a.silenceUntil ? <Typography variant="caption" color="text.secondary" component="div">silent until <TimeDisplay time={a.silenceUntil} /></Typography> : null}
        </Typography>
      ),
    },
    { id: 'count', label: 'Count', align: 'right', render: (a) => formatCount(a.count) },
    { id: 'first', label: 'First seen', render: (a) => <TimeDisplay time={a.firstSeen} variant="full" /> },
    { id: 'last', label: 'Last seen', render: (a) => <TimeDisplay time={a.lastSeen} variant="full" /> },
    {
      id: 'actions',
      label: 'Actions',
      align: 'right',
      render: (a) => (
        <Stack direction="row" spacing={0.25} justifyContent="flex-end">
          {a.status !== 'acknowledged' && a.status !== 'resolved' ? (
            <Tooltip title="Acknowledge (with reason)">
              <IconButton size="small" aria-label={`Acknowledge ${a.title}`} onClick={() => openAction({ kind: 'ack', alert: a })}>
                <CheckCircle fontSize="small" />
              </IconButton>
            </Tooltip>
          ) : null}
          {a.status === 'acknowledged' ? (
            <Tooltip title="Clear acknowledgement">
              <IconButton size="small" aria-label={`Clear acknowledgement for ${a.title}`} onClick={() => openAction({ kind: 'unack', alert: a })}>
                <Undo fontSize="small" />
              </IconButton>
            </Tooltip>
          ) : null}
          {a.status !== 'silenced' && a.status !== 'resolved' ? (
            <Tooltip title="Silence until…">
              <IconButton size="small" aria-label={`Silence ${a.title}`} onClick={() => openAction({ kind: 'silence', alert: a })}>
                <NotificationsOff fontSize="small" />
              </IconButton>
            </Tooltip>
          ) : null}
          {a.status === 'silenced' ? (
            <Tooltip title="End silence">
              <IconButton size="small" aria-label={`End silence for ${a.title}`} onClick={() => openAction({ kind: 'unsilence', alert: a })}>
                <VolumeOff fontSize="small" />
              </IconButton>
            </Tooltip>
          ) : null}
        </Stack>
      ),
    },
  ];

  const items = alerts.data?.items ?? [];

  return (
    <Box>
      <PageHeader
        title="Alerts"
        subtitle="Active, acknowledged, silenced, and historical alerts. Acknowledge and silence write audit records server-side."
        actions={
          <Paper variant="outlined" sx={{ p: 0.5 }}>
            <Stack direction="row" spacing={1}>
              <FormControl size="small" sx={{ minWidth: 180 }}>
                <InputLabel>Status</InputLabel>
                <Select
                  label="Status"
                  value={filters.status ?? ''}
                  onChange={(e) => setSearchParams(alertsToSearchParams({ ...filters, status: e.target.value || undefined }), { replace: true })}
                >
                  {ALERT_STATUSES.map((s) => (
                    <MenuItem key={s.value} value={s.value}>{s.label}</MenuItem>
                  ))}
                </Select>
              </FormControl>
              <FormControl size="small" sx={{ minWidth: 160 }}>
                <InputLabel>Host</InputLabel>
                <Select
                  label="Host"
                  value={filters.hostId ?? ''}
                  onChange={(e) => setSearchParams(alertsToSearchParams({ ...filters, hostId: e.target.value || undefined }), { replace: true })}
                >
                  <MenuItem value="">All hosts</MenuItem>
                  {(hosts.data ?? []).map((h) => (
                    <MenuItem key={h.id} value={h.id}>{h.name}</MenuItem>
                  ))}
                </Select>
              </FormControl>
            </Stack>
          </Paper>
        }
      />

      {alerts.isPending ? <LoadingState label="Loading alerts…" /> : null}
      {alerts.isError && !alerts.data ? <ErrorState error={alerts.error} onRetry={() => void alerts.refetch()} /> : null}

      {alerts.data && items.length === 0 ? (
        <EmptyState
          title={filters.status ? `No ${filters.status} alerts` : 'No alerts'}
          description="Alert rules evaluate health, events, heartbeats, thresholds, and VM heartbeats server-side."
        />
      ) : null}

      {items.length > 0 ? (
        <>
          {alerts.data?.hasMore ? (
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
              More alerts are available; narrow the filters to see them.
            </Typography>
          ) : null}
          <DataTable
            columns={columns}
            rows={items}
            rowKey={(a) => a.id ?? ''}
            maxHeight={640}
            aria-label="Alerts"
            getRowProps={() => ({ style: { cursor: 'default' } })}
          />
        </>
      ) : null}

      {action?.kind === 'ack' ? (
        <ConfirmDialog
          open
          title="Acknowledge alert?"
          body={`"${action.alert.title}" will be marked acknowledged. An audit record is written server-side.`}
          confirmLabel="Acknowledge"
          requireReason
          reasonLabel="Reason (required for audit)"
          busy={busy}
          onConfirm={(reason) => void runAction(reason)}
          onCancel={() => setAction(null)}
        />
      ) : null}

      {action?.kind === 'silence' ? (
        <ConfirmDialog
          open
          title="Silence alert?"
          body={`"${action.alert.title}" will be silenced until the time below.`}
          confirmLabel="Silence"
          busy={busy}
          onConfirm={(reason) => void runAction(reason)}
          onCancel={() => setAction(null)}
        >
          <TextField
            label="Silent until (local time)"
            type="datetime-local"
            fullWidth
            margin="dense"
            value={silenceUntil}
            onChange={(e) => setSilenceUntil(e.target.value)}
            InputLabelProps={{ shrink: true }}
            disabled={busy}
          />
        </ConfirmDialog>
      ) : null}

      {action?.kind === 'unack' ? (
        <ConfirmDialog
          open
          title="Clear acknowledgement?"
          body="The alert returns to active state and can be acknowledged again."
          confirmLabel="Clear acknowledgement"
          busy={busy}
          onConfirm={() => void runAction(undefined)}
          onCancel={() => setAction(null)}
        />
      ) : null}

      {action?.kind === 'unsilence' ? (
        <ConfirmDialog
          open
          title="End silence?"
          body="The alert becomes active again immediately."
          confirmLabel="End silence"
          busy={busy}
          onConfirm={() => void runAction(undefined)}
          onCancel={() => setAction(null)}
        />
      ) : null}

      {error ? <ErrorState compact error={error} /> : null}
    </Box>
  );
}
