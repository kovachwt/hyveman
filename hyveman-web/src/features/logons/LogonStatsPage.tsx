/**
 * Logon stats (FRONTEND.md §8.7): "who logged on where, how often" — per-user /
 * per-day aggregates derived server-side from curated Security events. Read-only;
 * the browser never re-aggregates events. Works globally (/logon-stats via
 * route-less access) and host-scoped (/hosts/:hostId/logons): the host page
 * resolves the host's source before querying (a host without a source yields
 * no rows, by design).
 */
import { useEffect, useMemo } from 'react';
import { useParams, useSearchParams } from 'react-router-dom';
import {
  Alert,
  Box,
  Card,
  CardContent,
  FormControl,
  InputLabel,
  MenuItem,
  Paper,
  Select,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import { useGetApiV1HostsId, useGetApiV1LogonStats } from '@/api';
import { DataTable, type Column } from '@/components/DataTable/DataTable';
import { LoadingState } from '@/components/LoadingState/LoadingState';
import { EmptyState } from '@/components/EmptyState/EmptyState';
import { ErrorState } from '@/components/ErrorState/ErrorState';
import { PageHeader } from '@/components/PageHeader/PageHeader';
import { Chart } from '@/components/Chart/Chart';
import { formatCount, utcDayLabel } from '@/lib/format';
import type { LogonStatDto } from '@/api/generated/endpoints';
import {
  emptyLogonStatsFilters,
  logonStatsFromSearchParams,
  logonStatsToApiParams,
  logonStatsToSearchParams,
  logonTotals,
  logonTypeLabel,
  normalizeLogonStatsFilters,
  perDaySeries,
  type LogonStatsFilters,
} from './logonStats';

export default function LogonStatsPage() {
  const { hostId } = useParams();
  const [searchParams, setSearchParams] = useSearchParams();

  // Host-scoped mode: resolve the host's associated source first. Without a
  // source there are no rows — show that clearly rather than an empty query.
  const host = useGetApiV1HostsId(hostId ?? '', {
    query: { select: (r) => r.data, enabled: Boolean(hostId) },
  });
  const hostSourceId = host.data?.sourceId ?? undefined;

  const filters = useMemo(
    () => normalizeLogonStatsFilters(logonStatsFromSearchParams(searchParams)),
    [searchParams],
  );

  // Host-scoped pages default to that host; the API filters by source exactly.
  const effectiveFilters = useMemo(() => {
    if (!hostId) return filters;
    return { ...filters, sourceId: hostSourceId };
  }, [hostId, filters, hostSourceId]);

  const apiParams = logonStatsToApiParams(effectiveFilters);

  const stats = useGetApiV1LogonStats(apiParams, {
    query: {
      select: (r) => r.data,
      enabled: !hostId || hostSourceId !== undefined,
    },
  });

  const totals = useMemo(() => logonTotals(stats.data?.items ?? []), [stats.data]);
  const series = useMemo(() => perDaySeries(stats.data?.items ?? []), [stats.data]);

  function commit(patch: Partial<LogonStatsFilters>) {
    const next = normalizeLogonStatsFilters({ ...filters, ...patch });
    setSearchParams(logonStatsToSearchParams(next), { replace: true });
  }

  // When a host-scoped page loads with no URL filters, seed sensible defaults.
  useEffect(() => {
    if (hostId && searchParams.size === 0 && hostSourceId !== undefined) {
      setSearchParams(logonStatsToSearchParams({ ...emptyLogonStatsFilters(), sourceId: hostSourceId }), { replace: true });
    }
  }, [hostId, hostSourceId, searchParams, setSearchParams]);

  const columns: Column<LogonStatDto>[] = [
    {
      id: 'day',
      label: 'Day (UTC)',
      always: true,
      render: (r) => <Typography variant="body2" sx={{ fontWeight: 600 }}>{utcDayLabel(r.day ?? '')}</Typography>,
    },
    {
      id: 'user',
      label: 'User',
      render: (r) => <Typography variant="body2" sx={{ fontFamily: 'monospace' }}>{r.user}</Typography>,
    },
    { id: 'type', label: 'Logon type', render: (r) => logonTypeLabel(r.logonType) },
    { id: 'success', label: 'Successes', align: 'right', render: (r) => formatCount(r.successCount) },
    { id: 'failure', label: 'Failures', align: 'right', render: (r) => formatCount(r.failureCount) },
  ];

  const header = hostId ? (
    <PageHeader
      title={`Logon stats — ${host.data?.name ?? hostId}`}
      subtitle="Per-user/per-day security logons (4624 interactive/RDP, 4625 failures, 4740 lockouts), derived server-side from curated Security events. Days are UTC."
    />
  ) : (
    <PageHeader
      title="Logon stats"
      subtitle="Per-user/per-day security logons (4624 interactive/RDP, 4625 failures, 4740 lockouts), derived server-side from curated Security events. Days are UTC."
    />
  );

  if (hostId && !host.isPending && host.isError && !host.data) {
    return (
      <Box>
        {header}
        <ErrorState error={host.error} onRetry={() => void host.refetch()} />
      </Box>
    );
  }

  if (hostId && hostSourceId === undefined && !host.isPending) {
    return (
      <Box>
        {header}
        <EmptyState
          title="Host has no agent source"
          description="Logon stats come from the host's associated source. Associate a source with this host on the hosts page."
        />
      </Box>
    );
  }

  return (
    <Box>
      {header}

      <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
        <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} flexWrap="wrap">
          <TextField
            label="From (UTC day)"
            type="date"
            size="small"
            value={filters.from ?? ''}
            onChange={(e) => commit({ from: e.target.value || undefined })}
            InputLabelProps={{ shrink: true }}
          />
          <TextField
            label="To (UTC day)"
            type="date"
            size="small"
            value={filters.to ?? ''}
            onChange={(e) => commit({ to: e.target.value || undefined })}
            InputLabelProps={{ shrink: true }}
          />
          <TextField
            label="User (exact match)"
            size="small"
            value={filters.user ?? ''}
            onChange={(e) => commit({ user: e.target.value || undefined })}
            sx={{ minWidth: 220 }}
          />
          <FormControl size="small" sx={{ minWidth: 180 }}>
            <InputLabel>Page size</InputLabel>
            <Select
              label="Page size"
              value={filters.limit ?? 50}
              onChange={(e) => commit({ limit: Number(e.target.value) })}
            >
              {[50, 100, 200].map((n) => (
                <MenuItem key={n} value={n}>{n}</MenuItem>
              ))}
            </Select>
          </FormControl>
          <Typography variant="caption" color="text.secondary" sx={{ alignSelf: 'center' }}>
            The API caps the page size at 200 and returns no cursor.
          </Typography>
        </Stack>
      </Paper>

      {stats.isPending ? <LoadingState label="Loading logon stats…" /> : null}
      {stats.isError && !stats.data ? <ErrorState error={stats.error} onRetry={() => void stats.refetch()} /> : null}

      {stats.data ? (
        <>
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ mb: 2 }}>
            <MiniStat label="Successful logons" value={totals.successes} />
            <MiniStat label="Failed logons" value={totals.failures} color="warning.main" />
            <MiniStat label="Lockouts" value={totals.lockouts} color="error.main" />
          </Stack>

          {(stats.data.items ?? []).length === 0 ? (
            <EmptyState
              title="No logon rows in range"
              description="The server aggregates curated Security events (4624 interactive/RDP, 4625, 4740). Widen the day range or clear the user filter."
            />
          ) : (
            <>
              {series.days.length > 1 ? (
                <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
                  <Typography variant="overline" color="text.secondary">Logons per day (UTC)</Typography>
                  <Chart
                    ariaLabel="Stacked bar chart of successful and failed logons per UTC day"
                    height={240}
                    option={{
                      grid: { left: 48, right: 16, top: 40, bottom: 48 },
                      tooltip: { trigger: 'axis' },
                      legend: { top: 4, data: ['Successes', 'Failures'] },
                      xAxis: {
                        type: 'category',
                        data: series.days.map(utcDayLabel),
                        axisLabel: { rotate: 45 },
                      },
                      yAxis: { type: 'value', minInterval: 1 },
                      series: [
                        { name: 'Successes', type: 'bar', stack: 'logons', data: series.successes, color: '#2e7d32' },
                        { name: 'Failures', type: 'bar', stack: 'logons', data: series.failures, color: '#b71c1c' },
                      ],
                    }}
                  />
                </Paper>
              ) : null}

              <DataTable
                columns={columns}
                rows={stats.data.items ?? []}
                rowKey={(r) => `${r.day}-${r.sourceId}-${r.user}-${r.logonType ?? 'lockout'}`}
                maxHeight={560}
                aria-label="Logon statistics"
                getRowProps={() => ({ style: { cursor: 'default' } })}
              />

              {stats.data.hasMore ? (
                <Alert severity="info" sx={{ mt: 2 }}>
                  More rows are available. This view has no cursor — narrow the filters (for
                  example a shorter UTC day range or a specific user) or raise the page size to
                  see them.
                </Alert>
              ) : null}
            </>
          )}
        </>
      ) : null}

      {/* Seed defaults for the global page on first visit. */}
      {!hostId && searchParams.size === 0 ? <SeedDefaults onSeed={() => setSearchParams(logonStatsToSearchParams(emptyLogonStatsFilters()), { replace: true })} /> : null}
    </Box>
  );

  function MiniStat({ label, value, color }: { label: string; value: number; color?: string }) {
    return (
      <Card sx={{ flex: 1 }}>
        <CardContent sx={{ py: 1.5, px: 2, '&:last-child': { pb: 1.5 } }}>
          <Typography variant="h5" sx={{ color, fontWeight: 700 }} aria-label={`${label}: ${value}`}>
            {formatCount(value)}
          </Typography>
          <Typography variant="body2" color="text.secondary">{label}</Typography>
        </CardContent>
      </Card>
    );
  }
}

/** Fills default filters on first visit (no URL state yet). */
function SeedDefaults({ onSeed }: { onSeed: () => void }) {
  useEffect(() => {
    const id = window.setTimeout(onSeed, 0);
    return () => window.clearTimeout(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  return null;
}
