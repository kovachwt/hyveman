/** Loading state (FRONTEND.md §10). */
import { Box, CircularProgress, Skeleton, Typography } from '@mui/material';

export function LoadingState({ label = 'Loading…', skeleton = false }: { label?: string; skeleton?: boolean }) {
  if (skeleton) {
    return (
      <Box sx={{ p: 2 }} role="status" aria-label={label} data-testid="loading-state">
        <Skeleton height={48} />
        <Skeleton height={48} />
        <Skeleton height={48} width="70%" />
      </Box>
    );
  }
  return (
    <Box
      role="status"
      aria-label={label}
      data-testid="loading-state"
      sx={{ display: 'flex', alignItems: 'center', gap: 2, py: 6, justifyContent: 'center', color: 'text.secondary' }}
    >
      <CircularProgress size={26} />
      <Typography variant="body2">{label}</Typography>
    </Box>
  );
}
