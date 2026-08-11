/** Host details (FRONTEND.md §8.2): rollup summary, component health table,
 *  server-bucketed health history charts, VM list, recent events, host-scoped
 *  logon stats, and alerts/maintenance. Charts request bounded ranges only. */
import { useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Alert,
  Box,
  Button,
  Chip,
  Grid,
  Paper,
  Stack,
  Tab,
  Tabs,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  useDeleteApiV1HostsIdIdracCert,
  useGetApiV1HostsId,
  useGetApiV1HostsIdHealth,
  useGetApiV1HostsIdHealthHistory,
  useGetApiV1HostsIdVms,
} from '@/api';
import { useQueryClient } from '@tanstack/react-query';
import { HealthBadge } from '@/components/HealthBadge/HealthBadge';
import { LoadingState } from '@/components/LoadingState/LoadingState';
import { ErrorState } from '@/components/ErrorState/ErrorState';
import { EmptyState } from '@/components/EmptyState/EmptyState';
import { PageHeader } from '@/components/PageHeader/PageHeader';
import { DataTable, type Column } from '@/components/DataTable/DataTable';
import { TimeDisplay } from '@/components/TimeDisplay/TimeDisplay';
import { Chart } from '@/components/Chart/Chart';
import { LogonStatsContent } from '@/features/logons/LogonStatsPage';
import { formatBytes, formatCount, formatDuration, formatPercent } from '@/lib/format';
import { healthPalette, normalizeHealthState } from '@/lib/health';
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

type TabId = 'summary' | 'components' | 'history' | 'vms' | 'events' | 'logons' | 'maintenance';

/** State ranks used to color the rollup heatmap band timeline. Critical is at
 *  rank 0 so it can be colored independently of any perceived "severity ladder";
 *  the heatmap visualMap maps each rank to its accent so each bucket is a
 *  single solid color with no gradient between states. */
const ROLLUP_RANK_LABELS = ['Critical', 'Warning', 'OK', 'Unknown'] as const;

export default function HostDetailPage() {
  const { hostId = '' } = useParams();
  const theme = useTheme();
  const palette = healthPalette(theme.palette.mode);
  // Semantic accents for the temp/power chart: warm for heat, cool for energy.
  // Resolved from the theme palette so they follow light/dark automatically.
  const tempColor = theme.palette.warning.main;
  const powerColor = theme.palette.info.main;
  const [tab, setTab] = useState<TabId>('summary');
  const [range, setRange] = useState<(typeof RANGES)[number]>(RANGES[0]);
  const queryClient = useQueryClient();

  // Current-state queries poll every 30 s while visible (FRONTEND.md §6.3).
  const host = useGetApiV1HostsId(hostId, {
    query: { select: (r) => r.data, refetchInterval: 30_000 },
  });
  const health = useGetApiV1HostsIdHealth(hostId, {
    query: { select: (r) => r.data, refetchInterval: 30_000 },
  });
  const vms = useGetApiV1HostsIdVms(hostId, {
    query: { select: (r) => r.data, refetchInterval: 30_000 },
  });

  // Clearing the accepted-on-first-use pin lets the next poll re-accept the
  // iDRAC certificate (e.g. after a Dell cert rotation).
  const clearPin = useDeleteApiV1HostsIdIdracCert({
    mutation: {
      onSuccess: () => {
        void queryClient.invalidateQueries({ queryKey: ['/api/v1/hosts', hostId] });
        void host.refetch();
      },
    },
  });

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
    {
      id: 'replication',
      label: 'Replication',
      render: (v) => <ReplicationCell vm={v} />,
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
  // Discrete-state band timeline: each server-bucketed snapshot becomes one
  // colored cell in a single-row heatmap, so "how long was it critical?" reads
  // at a glance and never inverts intuition the way the bar chart did (there,
  // the critical bar grew tallest, reading as "more = worse"). Each bucket is
  // a solid color — no gradient between critical and ok — and 'unknown'
  // renders as its own neutral cell rather than a spurious severity step.
  const rollupBuckets = points.map((p) => {
    const s = normalizeHealthState(p.rollupState);
    const rank: 0 | 1 | 2 | 3 = s === 'critical' ? 0 : s === 'warning' ? 1 : s === 'ok' ? 2 : 3;
    return { label: p.time ? new Date(p.time).toLocaleString() : '—', rank };
  });
  const rollupXLabels = rollupBuckets.map((b) => b.label);
  const rollupData = rollupBuckets.map((_, i) => [i, 0, rollupBuckets[i]!.rank]);

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
        <Tab label="Logons" value="logons" />
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
                <Row
                  label="Last poll"
                  value={
                    h.idracStatus?.lastPoll ? (
                      <Stack direction="row" spacing={1} alignItems="center">
                        <Chip
                          size="small"
                          color={h.idracStatus.lastPollOk ? 'success' : 'error'}
                          label={h.idracStatus.lastPollOk ? 'OK' : 'Failed'}
                        />
                        <TimeDisplay time={h.idracStatus.lastPoll} variant="full" typographyProps={{ variant: 'body2' }} />
                      </Stack>
                    ) : (
                      <Typography variant="body2" color="text.disabled">never polled</Typography>
                    )
                  }
                />
                {h.idracStatus?.lastError ? (
                  <Row
                    label="Error"
                    value={
                      <Typography variant="body2" color="error.main" sx={{ wordBreak: 'break-word' }}>
                        {h.idracStatus.lastError}
                      </Typography>
                    }
                  />
                ) : null}
                <Row
                  label="Cert pin"
                  value={
                    h.idracCert ? (
                      <Stack direction="row" spacing={1} alignItems="center">
                        <Tooltip
                          title={`SHA-256 ${h.idracCert.fingerprint} · accepted ${h.idracCert.acceptedAt ? new Date(h.idracCert.acceptedAt).toLocaleString() : 'unknown'}`}
                        >
                          <Typography variant="body2" sx={{ fontFamily: 'monospace' }}>
                            {(h.idracCert.fingerprint ?? '').slice(0, 16)}…
                          </Typography>
                        </Tooltip>
                        <Button size="small" onClick={() => clearPin.mutate({ id: hostId })} disabled={clearPin.isPending}>
                          Clear
                        </Button>
                      </Stack>
                    ) : (
                      <Typography variant="body2" color="text.disabled">not pinned</Typography>
                    )
                  }
                />
              </Stack>
            </Paper>
          </Grid>
          <Grid size={{ xs: 12, md: 6 }}>
            <Paper variant="outlined" sx={{ p: 2 }}>
              <Typography variant="overline" color="text.secondary">Latest metrics</Typography>
              {(health.data?.latestMetrics ?? []).length > 0 ? (
                <DataTable columns={metricColumns} rows={health.data?.latestMetrics ?? []} rowKey={(m) => `${m.name}-${m.time}`} maxHeight={260} aria-label="Latest metrics" disableBorder />
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
                <DataTable columns={alertColumns} rows={(h.recentAlerts ?? []).slice(0, 5)} rowKey={(a) => a.id ?? ''} maxHeight={260} aria-label="Recent alerts" getRowProps={() => ({ style: { cursor: 'default' } })} disableBorder />
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
          <ToggleButtonGroup
            size="small"
            exclusive
            value={range.label}
            aria-label="Health history range"
            onChange={(_, v) => {
              const r = RANGES.find((x) => x.label === v);
              if (r) setRange(r);
            }}
            sx={{ flexWrap: 'wrap' }}
          >
            {RANGES.map((r) => (
              <ToggleButton key={r.label} value={r.label} aria-label={r.label}>{r.label}</ToggleButton>
            ))}
          </ToggleButtonGroup>
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
                  height={140}
                  option={{
                    grid: { left: 8, right: 12, top: 12, bottom: 36 },
                    tooltip: {
                      trigger: 'item',
                      formatter: (params: unknown) => {
                        const p = (Array.isArray(params) ? params[0] : params) as
                          | { dataIndex?: number }
                          | undefined;
                        const b = p?.dataIndex != null ? rollupBuckets[p.dataIndex] : undefined;
                        return b ? `${b.label}<br/>${ROLLUP_RANK_LABELS[b.rank]}` : '';
                      },
                    },
                    xAxis: {
                      type: 'category',
                      data: rollupXLabels,
                      axisTick: { show: false },
                      splitArea: { show: false },
                      axisLabel: { hideOverlap: true },
                    },
                    yAxis: { type: 'category', data: ['State'], show: false },
                    visualMap: {
                      show: false,
                      type: 'piecewise',
                      min: 0,
                      max: 3,
                      pieces: [
                        { value: 0, color: palette.critical },
                        { value: 1, color: palette.warning },
                        { value: 2, color: palette.ok },
                        { value: 3, color: palette.neutral },
                      ],
                    },
                    series: [
                      {
                        type: 'heatmap',
                        data: rollupData,
                        itemStyle: { borderColor: theme.palette.background.paper, borderWidth: 1 },
                      },
                    ],
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
                      { type: 'value', name: '°C', nameTextStyle: { color: tempColor }, axisLine: { lineStyle: { color: tempColor } } },
                      { type: 'value', name: 'W', nameTextStyle: { color: powerColor }, axisLine: { lineStyle: { color: powerColor } } },
                    ],
                    series: [
                      {
                        name: 'Temperature max (°C)',
                        type: 'line',
                        showSymbol: false,
                        color: tempColor,
                        data: tempPowerSeries.xData.map((t, i) => [t, tempPowerSeries.temp[i]]),
                        markLine: {
                          silent: true,
                          symbol: ['none', 'none'],
                          lineStyle: { type: 'dashed', color: theme.palette.error.main },
                          data: [{ yAxis: 80, label: { formatter: '80°C' } }],
                        },
                      },
                      {
                        name: 'Power (W)',
                        type: 'line',
                        yAxisIndex: 1,
                        showSymbol: false,
                        color: powerColor,
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

      {tab === 'logons' ? (
        <LogonStatsContent hostId={h.id} />
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

function severityToState(severity: number | string | null | undefined): string {
  const n = typeof severity === 'string' ? Number(severity) : severity;
  if (n == null || Number.isNaN(n)) return 'unknown';
  if (n <= 2) return 'critical';
  if (n === 3) return 'warning';
  return 'ok';
}

/** Replication health cell (FRONTEND.md §8.2): a health badge when the VM is
 *  replicated, "—" when not. The tooltip carries the Hyper-V replication
 *  state and the last apply time. */
function ReplicationCell({ vm }: { vm: VmDto }) {
  const health = vm.replicationHealth;
  if (health == null || health === 'not_applicable') return <span aria-label="Not replicated">—</span>;
  const state = vm.replicationState ?? '';
  const lastApply = vm.replicationLastApplyTime
    ? new Date(vm.replicationLastApplyTime).toLocaleString()
    : null;
  const title = [
    state ? `state: ${state.replaceAll('_', ' ')}` : null,
    lastApply ? `last apply: ${lastApply}` : null,
  ].filter(Boolean).join(' · ');
  return <HealthBadge state={health} size="small" title={title || undefined} />;
}

// Host-scoped maintenance windows: the API has no host filter, so the list is
// filtered client-side (small fleet; windows are few).
import { MaintenanceSection } from '@/features/maintenance/MaintenanceSection';
