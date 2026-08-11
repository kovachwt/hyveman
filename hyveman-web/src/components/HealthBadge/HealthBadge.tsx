/** Health/severity status badge: color + icon + text label (FRONTEND.md §9). */
import CheckCircleOutline from '@mui/icons-material/CheckCircleOutline';
import ErrorOutline from '@mui/icons-material/ErrorOutline';
import HelpOutline from '@mui/icons-material/HelpOutline';
import WarningAmber from '@mui/icons-material/WarningAmber';
import Schedule from '@mui/icons-material/Schedule';
import type { SvgIconComponent } from '@mui/icons-material';
import { Box, Chip, Tooltip } from '@mui/material';
import { useTheme } from '@mui/material/styles';
import { healthLabel, healthPalette, normalizeHealthState, stateColor, type HealthState } from '@/lib/health';

const ICONS: Record<HealthState, SvgIconComponent> = {
  ok: CheckCircleOutline,
  warning: WarningAmber,
  critical: ErrorOutline,
  unknown: HelpOutline,
  stale: Schedule,
};

export interface HealthBadgeProps {
  /** API state string (rollup, component, agent status, severity). */
  state: string | null | undefined;
  /** Optional tooltip; defaults to the state label. */
  title?: string;
  size?: 'small' | 'medium';
  /** Label text to render instead of the derived one (e.g. severity words). */
  label?: string;
}

/** Badge for component/rollup health states. */
export function HealthBadge({ state, title, size = 'medium', label }: HealthBadgeProps) {
  const theme = useTheme();
  const healthState = normalizeHealthState(state);
  const text = label ?? healthLabel(healthState);
  const Icon = ICONS[healthState];
  const palette = healthPalette(theme.palette.mode);
  const color = stateColor(healthState, palette);

  return (
    <Tooltip title={title ?? `${text} (${state ?? 'no data'})`}>
      <Chip
        icon={<Icon fontSize={size === 'small' ? 'inherit' : 'small'} />}
        label={text}
        size={size}
        sx={{
          height: size === 'small' ? 24 : 28,
          color,
          borderColor: color,
          backgroundColor: `${color}${theme.palette.mode === 'dark' ? '29' : '1A'}`,
          '& .MuiChip-icon': { color },
          fontWeight: 600,
        }}
        variant="outlined"
      />
    </Tooltip>
  );
}

/** Bare icon+label row used inside tiles and dense tables. */
export function HealthGlyph({
  state,
  label,
}: {
  state: string | null | undefined;
  label?: string;
}) {
  const theme = useTheme();
  const healthState = normalizeHealthState(state);
  const text = label ?? healthLabel(healthState);
  const Icon = ICONS[healthState];
  const color = stateColor(healthState, healthPalette(theme.palette.mode));
  return (
    <Box component="span" sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.75, color }}>
      <Icon fontSize="small" aria-hidden />
      <span>{text}</span>
    </Box>
  );
}
