/** Relative + absolute time display with a UTC tooltip (FRONTEND.md §9):
 *  relative times always carry an absolute timestamp for precision. */
import { useEffect, useState } from 'react';
import { Tooltip, Typography, type TypographyProps } from '@mui/material';
import { formatDateTime, formatUtcDateTime, relativeTime } from '@/lib/format';

export interface TimeDisplayProps {
  time: string | null | undefined;
  /** compact: "2m ago · 14:32:05"; full adds the date. */
  variant?: 'compact' | 'full';
  now?: number;
  typographyProps?: TypographyProps;
}

export function TimeDisplay({ time, variant = 'compact', now: fixedNow, typographyProps }: TimeDisplayProps) {
  const [now, setNow] = useState(() => fixedNow ?? Date.now());

  // Refresh relative labels while mounted (30s tick).
  useEffect(() => {
    if (fixedNow !== undefined) return;
    const id = window.setInterval(() => setNow(Date.now()), 30_000);
    return () => window.clearInterval(id);
  }, [fixedNow]);

  if (!time) return <Typography {...typographyProps} sx={{ color: 'text.disabled', ...typographyProps?.sx }}>—</Typography>;

  const relative = relativeTime(time, now);
  const absolute = variant === 'compact' ? new Date(time).toLocaleTimeString() : formatDateTime(time);
  const utc = formatUtcDateTime(time);

  return (
    <Tooltip title={`${utc} (UTC)`}>
      <Typography
        component="span"
        variant="body2"
        sx={{ whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums', ...typographyProps?.sx }}
        {...typographyProps}
      >
        {relative} · {absolute}
      </Typography>
    </Tooltip>
  );
}
