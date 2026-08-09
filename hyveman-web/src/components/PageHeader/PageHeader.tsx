/** Page header with title, subtitle and action slot. */
import { Box, Typography } from '@mui/material';
import type { ReactNode } from 'react';

export function PageHeader({
  title,
  subtitle,
  actions,
}: {
  title: ReactNode;
  subtitle?: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <Box sx={{ display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', gap: 2, mb: 2, flexWrap: 'wrap' }}>
      <Box>
        <Typography variant="h5" component="h1">{title}</Typography>
        {subtitle ? (
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.25 }}>
            {subtitle}
          </Typography>
        ) : null}
      </Box>
      {actions ? <Box sx={{ display: 'flex', gap: 1, alignItems: 'center' }}>{actions}</Box> : null}
    </Box>
  );
}
