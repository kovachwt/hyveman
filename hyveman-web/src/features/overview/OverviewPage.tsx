/** Fleet overview dashboard (FRONTEND.md §8.1): host tiles, summary counts,
 *  agent/iDRAC staleness, and a stale-data banner. Polls every 30 s while
 *  visible; a refetch failure after success keeps the last data visible and
 *  labeled stale. Host tiles sort by severity (problems first); summary cards
 *  filter the grid, and the unacknowledged-alerts card links to the alerts
 *  view. */
import { useMemo, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  Card,
  CardActionArea,
  CardContent,
  Chip,
  CircularProgress,
  Grid,
  IconButton,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material';
import { Link } from 'react-router-dom';
import Refresh from '@mui/icons-material/Refresh';
import { useGetApiV1Overview } from '@/api';
import { numOr } from '@/api/dto';
import { useTheme } from '@mui/material/styles';
import type { HostTileDto } from '@/api/generated/endpoints';
import { HealthTile } from '@/components/HealthTile/HealthTile';
import { LoadingState } from '@/components/LoadingState/LoadingState';
import { EmptyState } from '@/components/EmptyState/EmptyState';
import { ErrorState } from '@/components/ErrorState/ErrorState';
import { PageHeader } from '@/components/PageHeader/PageHeader';
import { TimeDisplay } from '@/components/TimeDisplay/TimeDisplay';
import { healthPalette, normalizeHealthState, normalizeSeverity, severityLabel } from '@/lib/health';

const STALE_AFTER_MS = 2 * 60_000;

/** Local filter applied to the tile grid by the summary cards. */
type HostFilter = 'critical' | 'warning' | 'silent' | 'ok' | null;

function hostMatches(host: HostTileDto, filter: HostFilter): boolean {
  switch (filter) {
    case 'critical':
      return normalizeHealthState(host.rollupState) === 'critical';
    case 'warning':
      return normalizeHealthState(host.rollupState) === 'warning';
    case 'ok':
      return normalizeHealthState(host.rollupState) === 'ok';
    case 'silent':
      return host.agent?.status === 'silent';
    default:
      return true;
  }
}

/** Problems first: critical → silent agent → warning → stale → unknown → ok,
 *  alphabetical by name within a tier. A silent agent is surfaced ahead of
 *  warnings because an unreachable host is a more urgent signal. */
function severityRank(host: HostTileDto): number {
  const rollup = normalizeHealthState(host.rollupState);
  if (rollup === 'critical') return 0;
  if (host.agent?.status === 'silent') return 1;
  if (rollup === 'warning') return 2;
  if (rollup === 'stale') return 3;
  if (rollup === 'unknown') return 4;
  return 5;
}

function SummaryCard({
  label,
  value,
  color,
  onClick,
  active,
  to,
}: {
  label: string;
  value: number;
  color?: string;
  onClick?: () => void;
  active?: boolean;
  to?: string;
}) {
  const accessible = `${label}: ${value}`;
  const interactive = Boolean(to || onClick);
  const content = (
    <CardContent sx={{ py: 1.5, px: 2, '&:last-child': { pb: 1.5 } }}>
      <Typography variant="h4" component="div" sx={{ color, fontWeight: 700 }} aria-label={interactive ? undefined : accessible}>
        {value}
      </Typography>
      <Typography variant="body2" color="text.secondary">
        {label}
      </Typography>
    </CardContent>
  );

  return (
    <Card
      sx={{
        height: '100%',
        ...(active ? { outline: '2px solid', outlineColor: color ?? 'primary.main', outlineOffset: -2 } : {}),
      }}
    >
      {to ? (
        <CardActionArea component={Link} to={to} aria-label={accessible}>
          {content}
        </CardActionArea>
      ) : onClick ? (
        <CardActionArea onClick={onClick} aria-label={accessible}>
          {content}
        </CardActionArea>
      ) : (
        content
      )}
    </Card>
  );
}

export default function OverviewPage() {
  const overview = useGetApiV1Overview({
    query: {
      refetchInterval: 30_000,
      select: (res) => res.data,
    },
  });

  const data = overview.data;
  const [tileFilter, setTileFilter] = useState<HostFilter>(null);
  const stale = data ? Date.now() - new Date(data.generatedAt ?? '').getTime() > STALE_AFTER_MS : false;
  const summary = data?.summary ?? {};
  const n = (v: unknown) => numOr(v, 0);

  const sortedHosts = useMemo(() => {
    const hosts = (data?.hosts ?? []).slice();
    hosts.sort((a, b) => {
      const r = severityRank(a) - severityRank(b);
      if (r !== 0) return r;
      return (a.name ?? '').localeCompare(b.name ?? '');
    });
    return hosts;
  }, [data?.hosts]);

  const visibleHosts = useMemo(
    () => (tileFilter ? sortedHosts.filter((h) => hostMatches(h, tileFilter)) : sortedHosts),
    [sortedHosts, tileFilter],
  );

  // The kind chip is noise in a homogeneous fleet; only show it when kinds vary.
  const showKind = useMemo(() => {
    const kinds = new Set((data?.hosts ?? []).map((h) => h.kind).filter(Boolean));
    return kinds.size > 1;
  }, [data?.hosts]);

  const toggleFilter = (f: Exclude<HostFilter, null>) => setTileFilter((cur) => (cur === f ? null : f));

  if (overview.isPending) return <LoadingState label="Loading fleet overview…" />;

  if (overview.isError && !data) {
    return (
      <Box>
        <PageHeader title="Overview" />
        <ErrorState error={overview.error} onRetry={() => void overview.refetch()} />
      </Box>
    );
  }

  if (!data || (data.hosts ?? []).length === 0) {
    return (
      <Box>
        <PageHeader title="Overview" />
        <EmptyState
          title="No hosts registered"
          description="Register hosts under Admin → Sources & tokens, then add hosts on the Hosts page."
          action={<Link to="/hosts">Go to hosts</Link>}
        />
      </Box>
    );
  }

  const unacked = n(summary.unacknowledgedAlerts);
  const silentAgents = n(summary.silentAgents);

  return (
    <Box>
      <PageHeader
        title="Overview"
        subtitle={
          <span>
            Generated <TimeDisplay time={data.generatedAt} variant="relative" /> · auto-refreshes every 30s
          </span>
        }
        actions={
          <Tooltip title="Refresh now">
            <IconButton
              aria-label="Refresh overview"
              data-testid="overview-refresh"
              onClick={() => void overview.refetch()}
            >
              {overview.isFetching ? <CircularProgress size={20} /> : <Refresh />}
            </IconButton>
          </Tooltip>
        }
      />

      {overview.isError ? (
        <Alert severity="warning" sx={{ mb: 2 }} data-testid="overview-stale-banner" action={
          <Button color="inherit" size="small" onClick={() => void overview.refetch()}>Retry</Button>
        }>
          Refresh failed — showing the last successful overview, which may be stale.
        </Alert>
      ) : null}

      {stale ? (
        <Alert severity="warning" sx={{ mb: 2 }} data-testid="overview-age-banner">
          The API reports this overview was generated more than two minutes ago. Data may be stale.
        </Alert>
      ) : null}

      <Grid container spacing={2} sx={{ mb: 3 }}>
        <Grid size={{ xs: 6, sm: 4, md: 2 }}>
          <SummaryCard label="Hosts" value={n(summary.total)} onClick={() => setTileFilter(null)} active={tileFilter === null} />
        </Grid>
        <Grid size={{ xs: 6, sm: 4, md: 2 }}>
          <SummaryCard label="Critical" value={n(summary.critical)} color="error.main" onClick={() => toggleFilter('critical')} active={tileFilter === 'critical'} />
        </Grid>
        <Grid size={{ xs: 6, sm: 4, md: 2 }}>
          <SummaryCard label="Silent agents" value={silentAgents} color={silentAgents > 0 ? 'error.main' : undefined} onClick={() => toggleFilter('silent')} active={tileFilter === 'silent'} />
        </Grid>
        <Grid size={{ xs: 6, sm: 4, md: 2 }}>
          <SummaryCard label="Warning" value={n(summary.warning)} color="warning.main" onClick={() => toggleFilter('warning')} active={tileFilter === 'warning'} />
        </Grid>
        <Grid size={{ xs: 6, sm: 4, md: 2 }}>
          <SummaryCard label="Unacked alerts" value={unacked} color={unacked > 0 ? 'warning.main' : undefined} to="/alerts?status=active" />
        </Grid>
        <Grid size={{ xs: 6, sm: 4, md: 2 }}>
          <SummaryCard label="OK" value={n(summary.ok)} onClick={() => toggleFilter('ok')} active={tileFilter === 'ok'} />
        </Grid>
      </Grid>

      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center' }}>
        <Typography variant="overline" color="text.secondary">Hosts</Typography>
        {tileFilter ? (
          <Chip
            size="small"
            label={`Showing ${visibleHosts.length} of ${sortedHosts.length} hosts`}
            onDelete={() => setTileFilter(null)}
            data-testid="overview-filter-chip"
          />
        ) : null}
      </Stack>
      <Grid container spacing={2} sx={{ mt: 0.5 }}>
        {visibleHosts.map((host) => (
          <Grid size={{ xs: 12, sm: 6, lg: 4, xl: 3 }} key={host.id}>
            <HealthTile host={host} showKind={showKind} />
          </Grid>
        ))}
      </Grid>

      {(data.recentAlerts ?? []).length > 0 ? (
        <Box sx={{ mt: 4 }}>
          <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center' }}>
            <Typography variant="overline" color="text.secondary">Recent alerts</Typography>
            <Typography variant="body2" component={Link} to="/alerts" sx={{ color: 'primary.main' }}>
              View all alerts
            </Typography>
          </Stack>
          <Card>
            <CardContent sx={{ p: 0, '&:last-child': { pb: 0 } }}>
              <Stack divider={<Box sx={{ borderTop: '1px solid', borderColor: 'divider' }} />}>
                {(data.recentAlerts ?? []).slice(0, 5).map((alert) => (
                  <Box key={alert.id} sx={{ px: 2, py: 1.25, display: 'flex', gap: 1.5, alignItems: 'center' }}>
                    <SeverityDot severity={alert.severity} />
                    <Typography variant="body2" sx={{ fontWeight: 600, flexGrow: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                      {alert.title}
                    </Typography>
                    <Typography variant="body2" color="text.secondary" noWrap>
                      {alert.hostName ?? alert.sourceId ?? '—'}
                    </Typography>
                    <TimeDisplay time={alert.lastSeen} variant="relative" />
                  </Box>
                ))}
              </Stack>
            </CardContent>
          </Card>
        </Box>
      ) : null}
    </Box>
  );
}

/** Small colored severity indicator for recent-alert rows (label in tooltip;
 *  never color-only). */
function SeverityDot({ severity }: { severity: string | null | undefined }) {
  const theme = useTheme();
  const palette = healthPalette(theme.palette.mode);
  const s = normalizeSeverity(severity);
  const color = s === 'critical' ? palette.critical : s === 'warning' ? palette.warning : palette.neutral;
  return (
    <Tooltip title={severityLabel(severity)}>
      <Box
        component="span"
        aria-label={severityLabel(severity)}
        role="img"
        sx={{ width: 9, height: 9, borderRadius: '50%', bgcolor: color, flexShrink: 0 }}
      />
    </Tooltip>
  );
}