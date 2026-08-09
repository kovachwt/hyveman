/**
 * Non-blocking network-loss banner (FRONTEND.md §10). Data remains visible
 * and is labeled stale by each view's error handling; mutations can never
 * appear successful without an API response.
 */
import { useSyncExternalStore } from 'react';
import CloudOff from '@mui/icons-material/CloudOff';
import { Alert, Typography } from '@mui/material';

function subscribe(onChange: () => void): () => void {
  window.addEventListener('online', onChange);
  window.addEventListener('offline', onChange);
  return () => {
    window.removeEventListener('online', onChange);
    window.removeEventListener('offline', onChange);
  };
}

function getSnapshot(): boolean {
  return navigator.onLine;
}

export function ConnectionBanner() {
  const online = useSyncExternalStore(subscribe, getSnapshot, () => true);
  if (online) return null;
  return (
    <Alert
      severity="warning"
      data-testid="connection-banner"
      icon={<CloudOff fontSize="inherit" />}
      sx={{ borderRadius: 0, '& .MuiAlert-message': { width: '100%' } }}
    >
      <Typography variant="body2">
        No network connection. Showing cached data, which may be stale — changes cannot be saved
        until the connection returns.
      </Typography>
    </Alert>
  );
}
