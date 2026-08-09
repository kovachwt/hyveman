/** Fleet overview dashboard (FRONTEND.md §8.1): host tiles, summary counts,
 *  agent/iDRAC staleness, and a stale-data banner. Polls every 30 s while
 *  visible; a refetch failure after success keeps the last data visible and
 *  labeled stale. */
import { Alert, Box, Button, Card, CardContent, Grid, Stack, Typography } from '@mui/material';
import { useGetApiV1Overview } from '@/api';
import { numOr } from '@/api/dto';
import { HealthTile } from '@/components/HealthTile/HealthTile';
import { LoadingState } from '@/components/LoadingState/LoadingState';
import { EmptyState } from '@/components/EmptyState/EmptyState';
import { ErrorState } from '@/components/ErrorState/ErrorState';
import { PageHeader } from '@/components/PageHeader/PageHeader';
import { TimeDisplay } from '@/components/TimeDisplay/TimeDisplay';
import { Link } from 'react-router-dom';

const STALE_AFTER_MS = 2 * 60_000;

function SummaryCard({ label, value, color }: { label: string; value: number; color?: string }) {
  return (
    <Card>
      <CardContent sx={{ py: 1.5, px: 2, '&:last-child': { pb: 1.5 } }}>
        <Typography variant="h4" component="div" sx={{ color, fontWeight: 700 }} aria-label={`${label}: ${value}`}>
          {value}
        </Typography>
        <Typography variant="body2" color="text.secondary">
          {label}
        </Typography>
      </CardContent>
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
  const stale = data ? Date.now() - new Date(data.generatedAt ?? '').getTime() > STALE_AFTER_MS : false;
  const summary = data?.summary ?? {};
  const n = (v: unknown) => numOr(v, 0);

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

  return (
    <Box>
      <PageHeader
        title="Overview"
        subtitle={
          <span>
            Generated <TimeDisplay time={data.generatedAt} variant="full" />
          </span>
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
          <SummaryCard label="Hosts" value={n(summary.total)} />
        </Grid>
        <Grid size={{ xs: 6, sm: 4, md: 2 }}>
          <SummaryCard label="Critical" value={n(summary.critical)} color="error.main" />
        </Grid>
        <Grid size={{ xs: 6, sm: 4, md: 2 }}>
          <SummaryCard label="Warning" value={n(summary.warning)} color="warning.main" />
        </Grid>
        <Grid size={{ xs: 6, sm: 4, md: 2 }}>
          <SummaryCard label="Silent agents" value={n(summary.silentAgents)} color={n(summary.silentAgents) > 0 ? 'error.main' : undefined} />
        </Grid>
        <Grid size={{ xs: 6, sm: 4, md: 2 }}>
          <SummaryCard label="Unacknowledged alerts" value={n(summary.unacknowledgedAlerts)} color={n(summary.unacknowledgedAlerts) > 0 ? 'warning.main' : undefined} />
        </Grid>
        <Grid size={{ xs: 6, sm: 4, md: 2 }}>
          <SummaryCard label="OK" value={n(summary.ok)} />
        </Grid>
      </Grid>

      <Typography variant="overline" color="text.secondary">Hosts</Typography>
      <Grid container spacing={2}>
        {(data.hosts ?? []).map((host) => (
          <Grid size={{ xs: 12, sm: 6, lg: 4 }} key={host.id}>
            <HealthTile host={host} />
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
                  <Box key={alert.id} sx={{ px: 2, py: 1.25, display: 'flex', gap: 2, alignItems: 'center' }}>
                    <Typography variant="body2" sx={{ fontWeight: 600, flexGrow: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                      {alert.title}
                    </Typography>
                    <Typography variant="body2" color="text.secondary">
                      {alert.hostName ?? alert.sourceId ?? '—'}
                    </Typography>
                    <TimeDisplay time={alert.lastSeen} />
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
