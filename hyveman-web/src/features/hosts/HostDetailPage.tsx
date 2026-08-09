/** Host details (FRONTEND.md §8.2): rollup summary, component health table,
 *  server-bucketed health history charts, VM list, recent events, and
 *  host-scoped alerts/maintenance. Charts request bounded ranges only. */
import { useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Alert,
  Box,
  Button,
  Chip,
  Grid,
  MenuItem,
  Paper,
  Stack,
  Tab,
  Tabs,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  useGetApiV1HostsId,
  useGetApiV1HostsIdHealth,
  useGetApiV1HostsIdHealthHistory,
  useGetApiV1HostsIdVms,
} from '@/api';
import { HealthBadge } from '@/components/HealthBadge/HealthBadge';
import { LoadingState } from '@/components/LoadingState/LoadingState';
import { ErrorState } from '@/components/ErrorState/ErrorState';
import { EmptyState } from '@/components/EmptyState/EmptyState';
import { PageHeader } from '@/components/PageHeader/PageHeader';
import { DataTable, type Column } from '@/components/DataTable/DataTable';
import { TimeDisplay } from '@/components/TimeDisplay/TimeDisplay';
import { Chart } from '@/components/Chart/Chart';
import { formatBytes, formatCount, formatDuration, formatPercent } from '@/lib/format';
import { healthPalette } from '@/lib/health';
import { useTheme } from '@mui/material/styles';
import type {
  AlertDto,
  ComponentDto,
  EventDto,
  MetricDto,
  VmDto,
} from '@/api/generated/endpoints';

const RANGES = [
  { label: '24 hours', hours: 24 },
  { label: '7 days', hours: 24 * 7 },
  { label: '30 days', hours: 24 * 30 },
  { label: '90 days', hours: 24 * 90 },
  { label: '1 year', hours: 24 * 365 },
] as const;

type TabId = 'summary' | 'components' | 'history' | 'vms' | 'events' | 'maintenance';

function rollupStateValue(state: string | null | undefined): number {
  switch ((state ?? '').toLowerCase()) {
    case 'ok':
      return 1;
    case 'warning':
      return 2;
    case 'critical':
      return 3;
    default:
      return 0;
  }
}

export default function HostDetailPage() {
  const { hostId = '' } = useParams();
  const theme = useTheme();
  const palette = healthPalette(theme.palette.mode);
  const [tab, setTab] = useState<TabId>('summary');
  const [range, setRange] = useState<(typeof RANGES)[number]>(RANGES[0]);

  const host = useGetApiV1HostsId(hostId, { query: { select: (r) => r.data } });
  const health = useGetApiV1HostsIdHealth(hostId, { query: { select: (r) => r.data } });
  const vms = useGetApiV1HostsIdVms(hostId, { query: { select: (r) => r.data } });

  const historyParams = useMemo(() => {
    const to = new Date();
    const from = new Date(to.getTime() - range.hours * 3_600_000);
    return { from: from.toISOString(), to: to.toISOString(), resolution: 'auto' };
  }, [range]);
  const history = useGetApiV1HostsIdHealthHistory(hostId, historyParams, {
    query: { select: (r) => r.data },
  });

  if (host.isPending) return <LoadingState label="Loading host…" />;
  if (host.isError && !host.data) {
    return (
      <Box>
        <PageHeader title="Host" />
        <ErrorState error={host.error} onRetry={() => void host.refetch()} />
      </Box>
    );
  }
  if (!host.data) return null;

  const h = host.data;

  const componentColumns: Column<ComponentDto>[] = [
    { id: 'type', label: 'Type', always: true, render: (c) => c.type },
    { id: 'name', label: 'Name', render: (c) => <Tooltip title={c.name}><span>{c.name}</span></Tooltip> },
    { id: 'state', label: 'State', render: (c) => <HealthBadge state={c.state} size="small" /> },
    { id: 'detail', label: 'Detail', render: (c) => <Typography variant="body2" sx={{ whiteSpace: 'normal' }}>{c.detail ?? '—'}</Typography> },
    { id: 'seen', label: 'Last seen', render: (c) => <TimeDisplay time={c.lastSeen} variant="full" /> },
  ];

  const vmColumns: Column<VmDto>[] = [
    { id: 'name', label: 'Name', always: true, render: (v) => v.name },
    { id: 'state', label: 'State', render: (v) => v.state },
    {
      id: 'heartbeat',
      label: 'Heartbeat',
      render: (v) =>
        v.heartbeatOk == null ? '—' : v.heartbeatOk ? 'OK' : 'Lost',
    },
    { id: 'cpu', label: 'CPU', align: 'right', render: (v) => formatPercent(v.cpuPct) },
    { id: 'mem', label: 'Memory', align: 'right', render: (v) => (v.memMb != null ? formatBytes(Number(v.memMb) * 1024 * 1024) : '—') },
    { id: 'seen', label: 'Last seen', render: (v) => <TimeDisplay time={v.lastSeen} variant="full" /> },
    {
      id: 'stale',
      label: 'Stale',
      render: (v) => (v.stale ? <Chip label="Stale" size="small" color="warning" variant="outlined" /> : 'No'),
    },
  ];

  const metricColumns: Column<MetricDto>[] = [
    { id: 'name', label: 'Metric', render: (m) => m.name },
    { id: 'value', label: 'Value', align: 'right', render: (m) => `${formatCount(m.value, 1)} ${m.unit ?? ''}` },
    { id: 'time', label: 'Time', render: (m) => <TimeDisplay time={m.time} variant="full" /> },
  ];

  const eventColumns: Column<EventDto>[] = [
    { id: 'time', label: 'Time', always: true, render: (e) => <TimeDisplay time={e.time} /> },
    { id: 'channel', label: 'Channel', render: (e) => e.channel ?? '—' },
    { id: 'severity', label: 'Sev.', render: (e) => <HealthBadge state={severityToState(e.severity)} size="small" label={String(e.severity ?? '')} /> },
    { id: 'eventId', label: 'Event ID', render: (e) => String(e.eventId ?? '—') },
    { id: 'message', label: 'Message', render: (e) => <Typography variant="body2" sx={{ whiteSpace: 'normal' }}>{e.message ?? ''}</Typography> },
  ];

  const alertColumns: Column<AlertDto>[] = [
    { id: 'title', label: 'Alert', always: true, render: (a) => a.title },
    { id: 'severity', label: 'Severity', render: (a) => <HealthBadge state={a.severity} size="small" label={a.severity} /> },
    { id: 'status', label: 'Status', render: (a) => a.status },
    { id: 'count', label: 'Count', align: 'right', render: (a) => formatCount(a.count) },
    { id: 'last', label: 'Last seen', render: (a) => <TimeDisplay time={a.lastSeen} variant="full" /> },
  ];

  const points = history.data?.points ?? [];
  const stateSeries = points.map((p) => ({
    value: [p.time ? new Date(p.time).getTime() : 0, rollupStateValue(p.rollupState)],
    itemStyle: { color: palette[rollupStateColorKey(p.rollupState)] },
  }));

  const tempPowerSeries = {
    xData: points.map((p) => (p.time ? new Date(p.time).getTime() : 0)),
    temp: points.map((p) => p.temperatureMaxC),
    power: points.map((p) => p.powerWatts),
  };

  const agent = h.agent;

  return (
    <Box>
      <PageHeader
        title={h.name}
        subtitle={
          <Stack direction="row" spacing={1} alignItems="center" sx={{ mt: 0.5 }}>
            <Chip label={h.kind} size="small" variant="outlined" />
            <HealthBadge state={h.rollupState} size="small" />
            <Typography variant="caption" color="text.secondary">
              rollup {h.rollupAt ? <TimeDisplay time={h.rollupAt} /> : 'never'}
            </Typography>
          </Stack>
        }
        actions={
          <Stack direction="row" spacing={1}>
            <Button component={Link} to={`/hosts/${h.id ?? ''}/logons`}>Logon stats</Button>
            <Button component={Link} to={`/logs?hostId=${encodeURIComponent(h.id ?? '')}`} variant="outlined">
              Search events
            </Button>
          </Stack>
        }
      />

      <Tabs
        value={tab}
        onChange={(_, v: TabId) => setTab(v)}
        variant="scrollable"
        scrollButtons="auto"
        aria-label="Host sections"
        sx={{ mb: 2, borderBottom: '1px solid', borderColor: 'divider' }}
      >
        <Tab label="Summary" value="summary" />
        <Tab label="Components" value="components" />
        <Tab label="Health history" value="history" />
        <Tab label="VMs" value="vms" />
        <Tab label="Events" value="events" />
        <Tab label="Maintenance" value="maintenance" />
      </Tabs>

      {tab === 'summary' ? (
        <Grid container spacing={2}>
          <Grid size={{ xs: 12, md: 6 }}>
            <Paper variant="outlined" sx={{ p: 2 }}>
              <Typography variant="overline" color="text.secondary">Agent</Typography>
              {agent ? (
                <Stack spacing={0.75} sx={{ mt: 0.5 }}>
                  <Row label="Status" value={<HealthBadge state={agent.status} size="small" label={agent.status === 'online' ? 'Online' : agent.status === 'silent' ? 'Silent' : 'Unknown'} />} />
                  <Row label="Version" value={agent.agentVersion ?? '—'} />
                  <Row label="OS build" value={agent.osBuild ?? '—'} />
                  <Row label="Uptime" value={agent.uptimeS != null ? formatDuration(agent.uptimeS) : '—'} />
                  <Row label="Boot time" value={agent.bootTime ? <TimeDisplay time={agent.bootTime} variant="full" /> : '—'} />
                  <Row label="Last heartbeat" value={agent.lastReceived ? <TimeDisplay time={agent.lastReceived} variant="full" /> : '—'} />
                  <Row label="VM count" value={formatCount(agent.vmCount)} />
                  {agent.degraded ? <Row label="Degraded" value={<Chip label={agent.degraded} size="small" color="warning" />} /> : null}
                  {agent.factsStale ? (
                    <Alert severity="warning" sx={{ mt: 1 }}>Agent facts are stale.</Alert>
                  ) : null}
                </Stack>
              ) : (
                <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
                  No agent source associated with this host.
                </Typography>
              )}
            </Paper>
          </Grid>
          <Grid size={{ xs: 12, md: 6 }}>
            <Paper variant="outlined" sx={{ p: 2 }}>
              <Typography variant="overline" color="text.secondary">Hardware (iDRAC)</Typography>
              <Stack spacing={0.75} sx={{ mt: 0.5 }}>
                <Row label="Configured" value={h.idracUrl ? 'Yes' : 'No'} />
                <Row label="URL" value={h.idracUrl ?? '—'} />
                <Row label="Credentials" value={h.idracCredentialSet ? 'Set' : 'Not set'} />
              </Stack>
            </Paper>
          </Grid>
          <Grid size={{ xs: 12, md: 6 }}>
            <Paper variant="outlined" sx={{ p: 2 }}>
              <Typography variant="overline" color="text.secondary">Latest metrics</Typography>
              {(health.data?.latestMetrics ?? []).length > 0 ? (
                <DataTable columns={metricColumns} rows={health.data?.latestMetrics ?? []} rowKey={(m) => `${m.name}-${m.time}`} maxHeight={260} aria-label="Latest metrics" />
              ) : (
                <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
                  No metrics yet.
                </Typography>
              )}
            </Paper>
          </Grid>
          <Grid size={{ xs: 12, md: 6 }}>
            <Paper variant="outlined" sx={{ p: 2 }}>
              <Typography variant="overline" color="text.secondary">Recent alerts</Typography>
              {(h.recentAlerts ?? []).length > 0 ? (
                <DataTable columns={alertColumns} rows={(h.recentAlerts ?? []).slice(0, 5)} rowKey={(a) => a.id ?? ''} maxHeight={260} aria-label="Recent alerts" getRowProps={() => ({ style: { cursor: 'default' } })} />
              ) : (
                <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
                  No recent alerts.
                </Typography>
              )}
            </Paper>
          </Grid>
        </Grid>
      ) : null}

      {tab === 'components' ? (
        health.isPending ? <LoadingState label="Loading components…" /> :
        health.isError && !health.data ? <ErrorState error={health.error} onRetry={() => void health.refetch()} /> :
        (health.data?.components ?? []).length === 0 ? (
          <EmptyState title="No component data" description="The first successful iDRAC poll populates component health. Check the summary tab for poll status." />
        ) : health.data ? (
          <DataTable columns={componentColumns} rows={health.data.components ?? []} rowKey={(c) => `${c.type}-${c.name}`} virtualize={(health.data.components ?? []).length > 100} maxHeight={640} aria-label="Component health" />
        ) : null
      ) : null}

      {tab === 'history' ? (
        <Stack spacing={2}>
          <TextField
            select
            label="Range"
            size="small"
            value={range.label}
            onChange={(e) => setRange(RANGES.find((r) => r.label === e.target.value) ?? RANGES[0])}
            sx={{ width: 180 }}
          >
            {RANGES.map((r) => (
              <MenuItem key={r.label} value={r.label}>{r.label}</MenuItem>
            ))}
          </TextField>
          {history.isPending ? <LoadingState label="Loading health history…" /> : null}
          {history.isError && !history.data ? <ErrorState error={history.error} onRetry={() => void history.refetch()} /> : null}
          {history.data && (history.data.points ?? []).length === 0 ? (
            <EmptyState title="No history in range" description="Try a shorter range; the API buckets snapshots server-side." />
          ) : null}
          {history.data && (history.data.points ?? []).length > 0 ? (
            <>
              <Paper variant="outlined" sx={{ p: 2 }}>
                <Typography variant="overline" color="text.secondary">Rollup state (server-bucketed)</Typography>
                <Chart
                  ariaLabel="Health rollup state over time"
                  height={220}
                  option={{
                    grid: { left: 48, right: 16, top: 24, bottom: 28 },
                    tooltip: { trigger: 'axis' },
                    xAxis: { type: 'time' },
                    yAxis: {
                      type: 'category',
                      data: ['unknown', 'ok', 'warning', 'critical'],
                      min: 0,
                      max: 3,
                    },
                    series: [{ type: 'bar', data: stateSeries, barWidth: '70%' }],
                  }}
                />
              </Paper>
              <Paper variant="outlined" sx={{ p: 2 }}>
                <Typography variant="overline" color="text.secondary">Temperature & power</Typography>
                <Chart
                  ariaLabel="Temperature and power over time"
                  height={260}
                  option={{
                    grid: { left: 48, right: 48, top: 40, bottom: 28 },
                    tooltip: { trigger: 'axis' },
                    legend: { top: 4, data: ['Temperature max (°C)', 'Power (W)'] },
                    xAxis: { type: 'time' },
                    yAxis: [
                      { type: 'value', name: '°C' },
                      { type: 'value', name: 'W' },
                    ],
                    series: [
                      {
                        name: 'Temperature max (°C)',
                        type: 'line',
                        showSymbol: false,
                        data: tempPowerSeries.xData.map((t, i) => [t, tempPowerSeries.temp[i]]),
                      },
                      {
                        name: 'Power (W)',
                        type: 'line',
                        yAxisIndex: 1,
                        showSymbol: false,
                        data: tempPowerSeries.xData.map((t, i) => [t, tempPowerSeries.power[i]]),
                      },
                    ],
                  }}
                />
              </Paper>
            </>
          ) : null}
        </Stack>
      ) : null}

      {tab === 'vms' ? (
        vms.isPending ? <LoadingState label="Loading VMs…" /> :
        vms.isError && !vms.data ? <ErrorState error={vms.error} onRetry={() => void vms.refetch()} /> :
        vms.data && vms.data.length === 0 ? (
          <EmptyState title="No VMs reported" description="The agent reports Hyper-V virtual machines with its telemetry facts." />
        ) : vms.data ? (
          <DataTable columns={vmColumns} rows={vms.data ?? []} rowKey={(v) => v.name ?? ''} maxHeight={560} aria-label="Virtual machines" />
        ) : null
      ) : null}

      {tab === 'events' ? (
        (h.recentEvents ?? []).length === 0 ? (
          <EmptyState title="No recent events" description="Agent-ingested events appear here. Use the event log for full search." />
        ) : (
          <DataTable columns={eventColumns} rows={h.recentEvents ?? []} rowKey={(e) => e.id ?? ''} maxHeight={560} aria-label="Recent events" getRowProps={() => ({ style: { cursor: 'default' } })} />
        )
      ) : null}

      {tab === 'maintenance' ? (
        <MaintenanceSection hostId={h.id} hostName={h.name} />
      ) : null}
    </Box>
  );

  function Row({ label, value }: { label: string; value: React.ReactNode }) {
    return (
      <Stack direction="row" sx={{ justifyContent: 'space-between', gap: 2 }}>
        <Typography variant="body2" color="text.secondary">{label}</Typography>
        <Typography variant="body2" component="span" sx={{ textAlign: 'right' }}>{value}</Typography>
      </Stack>
    );
  }
}

function rollupStateColorKey(state: string | null | undefined): 'ok' | 'warning' | 'critical' | 'neutral' {
  switch ((state ?? '').toLowerCase()) {
    case 'ok':
      return 'ok';
    case 'warning':
      return 'warning';
    case 'critical':
      return 'critical';
    default:
      return 'neutral';
  }
}

function severityToState(severity: number | string | null | undefined): string {
  const n = typeof severity === 'string' ? Number(severity) : severity;
  if (n == null || Number.isNaN(n)) return 'unknown';
  if (n <= 2) return 'critical';
  if (n === 3) return 'warning';
  return 'ok';
}

// Host-scoped maintenance windows: the API has no host filter, so the list is
// filtered client-side (small fleet; windows are few).
import { MaintenanceSection } from '@/features/maintenance/MaintenanceSection';
