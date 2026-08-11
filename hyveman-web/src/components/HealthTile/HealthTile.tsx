/** Overview host tile (FRONTEND.md §8.1): name, kind, rollup health, the
 *  Hardware/OS breakdown, agent heartbeat age, iDRAC poll state, and active
 *  alert count. Clicking navigates to /hosts/:id. (Hyper-V health has no
 *  state on the overview; VM health lives on the host detail VMs tab.) */
import { Box, Card, CardActionArea, CardContent, Chip, Stack, Tooltip, Typography, useTheme } from '@mui/material';
import { useNavigate } from 'react-router-dom';
import CheckCircleOutline from '@mui/icons-material/CheckCircleOutline';
import NotificationsActive from '@mui/icons-material/NotificationsActive';
import Schedule from '@mui/icons-material/Schedule';
import type { HostTileDto } from '@/api/generated/endpoints';
import { numOr } from '@/api/dto';
import { HealthBadge } from '@/components/HealthBadge/HealthBadge';
import { TimeDisplay } from '@/components/TimeDisplay/TimeDisplay';
import { agentStatusLabel, healthPalette, normalizeHealthState, stateColor } from '@/lib/health';

function SectionRow({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', gap: 1 }}>
      <Typography variant="body2" color="text.secondary" sx={{ minWidth: 92 }}>
        {label}
      </Typography>
      <Typography variant="body2" component="span" sx={{ textAlign: 'right' }}>
        {value}
      </Typography>
    </Stack>
  );
}

export interface HealthTileProps {
  host: HostTileDto;
  /** Hide the kind chip when the fleet is homogeneous. Defaults to shown. */
  showKind?: boolean;
}

export function HealthTile({ host, showKind = true }: HealthTileProps) {
  const theme = useTheme();
  const navigate = useNavigate();
  const rollup = normalizeHealthState(host.rollupState);
  const palette = healthPalette(theme.palette.mode);
  const agent = host.agent;
  const idrac = host.idrac;
  const agentState = normalizeHealthState(agent?.status);

  // Reserve the colored left rail for problems; healthy/unknown stay calm so a
  // scanning eye lands on red instantly.
  const isProblem = rollup === 'critical' || rollup === 'warning';
  const alertCount = numOr(host.activeAlertCount, 0);

  // Surface staleness on the tile itself: the rollup-evaluated time is the
  // strongest "is this current?" signal, so it should not be the lowest-
  // emphasis caption (text.disabled) as it previously was. Tint it red once a
  // few poll cycles have passed without a fresh evaluation.
  const rollupAgeMs = host.rollupAt ? Date.now() - new Date(host.rollupAt).getTime() : Number.POSITIVE_INFINITY;
  const rollupStale = rollupAgeMs > 10 * 60_000;
  const rollupCaptionColor = rollupStale ? 'error.main' : 'text.secondary';

  return (
    <Card
      variant="outlined"
      sx={{
        height: '100%',
        ...(isProblem ? { borderLeft: '4px solid', borderLeftColor: stateColor(rollup, palette) } : {}),
      }}
    >
      <CardActionArea
        onClick={() => navigate(`/hosts/${host.id}`)}
        aria-label={`Open ${host.name} details`}
        sx={{ height: '100%' }}
      >
        <CardContent sx={{ p: 1.5, '&:last-child': { pb: 1.5 } }}>
          <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'flex-start', gap: 1, mb: 1 }}>
            <Typography variant="h6" component="h2" noWrap sx={{ maxWidth: showKind ? '60%' : '100%' }}>
              {host.name}
            </Typography>
            {showKind && host.kind ? (
              <Chip label={host.kind} size="small" variant="outlined" sx={{ textTransform: 'capitalize' }} />
            ) : null}
          </Stack>

          <HealthBadge state={host.rollupState} size="small" />

          <Stack spacing={0.5} sx={{ mt: 1 }}>
            {/* Hardware health comes from iDRAC/Redfish; when iDRAC is not
                configured the state can never be known, so say so plainly
                instead of stacking gray "Unknown" badges. */}
            <SectionRow
              label="Hardware"
              value={
                !idrac?.configured ? (
                  <Typography variant="body2" color="text.disabled">Not configured</Typography>
                ) : (
                  <HealthBadge state={host.hardwareState} size="small" />
                )
              }
            />
            <SectionRow label="OS" value={<HealthBadge state={host.osState} size="small" />} />
            <SectionRow
              label="Agent"
              value={
                agent ? (
                  <Tooltip title={`Version ${agent.agentVersion ?? 'unknown'}`}>
                    <HealthBadge state={agent.status} size="small" label={agentStatusLabel(agent.status)} />
                  </Tooltip>
                ) : (
                  <Typography variant="body2" color="text.disabled">No agent</Typography>
                )
              }
            />
          </Stack>

          <Stack spacing={0.5} sx={{ mt: 1.5 }}>
            <SectionRow
              label="Heartbeat"
              value={
                agent?.lastReceived ? (
                  <TimeDisplay
                    time={agent.lastReceived}
                    variant="relative"
                    typographyProps={{
                      variant: 'body2',
                      sx: { color: agentState === 'critical' ? 'error.main' : agentState === 'ok' ? undefined : 'text.disabled' },
                    }}
                  />
                ) : (
                  <Typography variant="body2" color="text.disabled">never</Typography>
                )
              }
            />
            <SectionRow
              label="iDRAC poll"
              value={
                !idrac?.configured ? (
                  <Typography variant="body2" color="text.disabled">not configured</Typography>
                ) : idrac.lastPoll ? (
                  <Tooltip title={idrac.lastPollOk ? 'Last poll succeeded' : `Last poll failed: ${idrac.lastError ?? 'unknown error'}`}>
                    <Typography variant="body2" sx={{ whiteSpace: 'nowrap', color: idrac.lastPollOk ? undefined : 'error.main' }}>
                      {idrac.lastPollOk ? 'OK' : 'Failed'} · <TimeDisplay time={idrac.lastPoll} variant="relative" />
                    </Typography>
                  </Tooltip>
                ) : (
                  <Typography variant="body2" color="text.disabled">never polled</Typography>
                )
              }
            />
          </Stack>

          <Stack direction="row" sx={{ mt: 1.5, alignItems: 'center', gap: 0.5, color: 'text.secondary' }}>
            {alertCount > 0 ? (
              <Tooltip title={`${alertCount} active alerts`}>
                <Typography variant="body2" sx={{ display: 'flex', alignItems: 'center', gap: 0.5, color: 'error.main', fontWeight: 600 }}>
                  <NotificationsActive fontSize="small" />
                  {alertCount} active alert{alertCount === 1 ? '' : 's'}
                </Typography>
              </Tooltip>
            ) : (
              <Typography variant="body2" sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
                <CheckCircleOutline fontSize="small" />
                No active alerts
              </Typography>
            )}
            <Box component="span" sx={{ ml: 'auto', display: 'inline-flex', alignItems: 'center', gap: 0.5, color: rollupCaptionColor }}>
              <Schedule fontSize="inherit" />
              {host.rollupAt ? (
                <>
                  <Typography component="span" variant="caption" sx={{ lineHeight: 'inherit' }}>Last evaluated</Typography>
                  <TimeDisplay
                    time={host.rollupAt}
                    variant="relative"
                    typographyProps={{ variant: 'caption', sx: { fontVariantNumeric: 'tabular-nums' } }}
                  />
                </>
              ) : (
                <Typography component="span" variant="caption" sx={{ lineHeight: 'inherit' }}>Not yet evaluated</Typography>
              )}
            </Box>
          </Stack>
        </CardContent>
      </CardActionArea>
    </Card>
  );
}