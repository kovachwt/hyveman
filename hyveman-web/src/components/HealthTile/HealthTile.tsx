/** Overview host tile (FRONTEND.md §8.1): name, kind, rollup health, the
 *  Hardware/OS/Hyper-V breakdown, agent heartbeat age, iDRAC poll state, and
 *  active alert count. Clicking navigates to /hosts/:id. */
import { Card, CardActionArea, CardContent, Chip, Stack, Tooltip, Typography } from '@mui/material';
import { useNavigate } from 'react-router-dom';
import ErrorOutline from '@mui/icons-material/ErrorOutline';
import NotificationsActive from '@mui/icons-material/NotificationsActive';
import type { HostTileDto } from '@/api/generated/endpoints';
import { numOr } from '@/api/dto';
import { HealthBadge } from '@/components/HealthBadge/HealthBadge';
import { TimeDisplay } from '@/components/TimeDisplay/TimeDisplay';
import { agentStatusLabel, healthLabel, normalizeHealthState } from '@/lib/health';

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

export function HealthTile({ host }: { host: HostTileDto }) {
  const navigate = useNavigate();
  const rollup = normalizeHealthState(host.rollupState);
  const agent = host.agent;
  const idrac = host.idrac;

  return (
    <Card variant="outlined" sx={{ height: '100%' }}>
      <CardActionArea
        onClick={() => navigate(`/hosts/${host.id}`)}
        aria-label={`Open ${host.name} details`}
        sx={{ height: '100%' }}
      >
        <CardContent>
          <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'flex-start', gap: 1, mb: 1 }}>
            <Typography variant="h6" component="h2" noWrap sx={{ maxWidth: '60%' }}>
              {host.name}
            </Typography>
            <Chip label={host.kind} size="small" variant="outlined" sx={{ textTransform: 'capitalize' }} />
          </Stack>

          <HealthBadge state={host.rollupState} size="small" />

          <Stack spacing={0.5} sx={{ mt: 1 }}>
            <SectionRow
              label="Hardware"
              value={<HealthBadge state={host.hardwareState} size="small" />}
            />
            <SectionRow label="OS" value={<HealthBadge state={host.osState} size="small" />} />
            <SectionRow
              label="Hyper-V"
              value={host.hyperVState ? <HealthBadge state={host.hyperVState} size="small" /> : <Typography variant="body2" color="text.disabled">—</Typography>}
            />
            <SectionRow
              label="Agent"
              value={
                <Tooltip title={agent ? `Version ${agent.agentVersion ?? 'unknown'}` : 'No agent associated'}>
                  <HealthBadge state={agent?.status} size="small" label={agentStatusLabel(agent?.status)} />
                </Tooltip>
              }
            />
          </Stack>

          <Stack spacing={0.5} sx={{ mt: 1.5 }}>
            <SectionRow
              label="Heartbeat"
              value={
                agent?.lastReceived ? (
                  <TimeDisplay time={agent.lastReceived} variant="full" typographyProps={{ variant: 'body2' }} />
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
                    <Typography variant="body2" sx={{ color: idrac.lastPollOk ? undefined : 'error.main' }}>
                      {idrac.lastPollOk ? 'OK' : 'Failed'} · <TimeDisplay time={idrac.lastPoll} variant="full" />
                    </Typography>
                  </Tooltip>
                ) : (
                  <Typography variant="body2" color="text.disabled">never polled</Typography>
                )
              }
            />
          </Stack>

          <Stack direction="row" sx={{ mt: 1.5, alignItems: 'center', gap: 0.5, color: 'text.secondary' }}>
            {numOr(host.activeAlertCount, 0) > 0 ? (
              <Tooltip title={`${numOr(host.activeAlertCount, 0)} active alerts`}>
                <Typography variant="body2" sx={{ display: 'flex', alignItems: 'center', gap: 0.5, color: 'error.main', fontWeight: 600 }}>
                  <NotificationsActive fontSize="small" />
                  {numOr(host.activeAlertCount, 0)} active alert{numOr(host.activeAlertCount, 0) === 1 ? '' : 's'}
                </Typography>
              </Tooltip>
            ) : (
              <Typography variant="body2" sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
                <ErrorOutline fontSize="small" />
                No active alerts
              </Typography>
            )}
            <Typography variant="caption" color="text.disabled" sx={{ ml: 'auto' }}>
              {healthLabel(rollup)} · rollup {host.rollupAt ? <TimeDisplay time={host.rollupAt} /> : 'never'}
            </Typography>
          </Stack>
        </CardContent>
      </CardActionArea>
    </Card>
  );
}
