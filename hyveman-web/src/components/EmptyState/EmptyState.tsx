/** Empty result state (FRONTEND.md §10). */
import InboxOutlined from '@mui/icons-material/InboxOutlined';
import { Box, Typography } from '@mui/material';
import type { ReactNode } from 'react';

export function EmptyState({
  title = 'Nothing here yet',
  description,
  action,
}: {
  title?: string;
  description?: ReactNode;
  action?: ReactNode;
}) {
  return (
    <Box
      data-testid="empty-state"
      sx={{ py: 6, display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 1, color: 'text.secondary', textAlign: 'center' }}
    >
      <InboxOutlined sx={{ fontSize: 44, opacity: 0.5 }} />
      <Typography variant="h6" sx={{ color: 'text.primary' }}>{title}</Typography>
      {description ? <Typography variant="body2" sx={{ maxWidth: 480 }}>{description}</Typography> : null}
      {action ? <Box sx={{ mt: 1 }}>{action}</Box> : null}
    </Box>
  );
}
