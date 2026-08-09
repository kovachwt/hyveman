/** Error state with retry (FRONTEND.md §10). Branches on stable API codes,
 *  never on human-readable detail text. */
import { Alert, AlertTitle, Box, Button, Typography } from '@mui/material';
import { ApiError } from '@/api/client';

/** User-friendly message for an API error, keyed off the stable `code`. */
export function apiErrorMessage(err: unknown): string {
  if (err instanceof ApiError) {
    switch (err.code) {
      case 'network_error':
        return err.message;
      case 'unauthorized':
      case 'session_expired':
        return 'Your session has expired. Sign in again.';
      case 'validation_failed':
        return err.detail ?? 'One or more fields are invalid.';
      case 'conflict':
        return err.detail ?? 'The resource changed since it was loaded. Reload and try again.';
      case 'not_found':
        return 'The requested resource was not found.';
      case 'rate_limited':
      case 'too_many_requests':
        return 'Too many requests. Wait a moment and retry.';
      case 'origin_not_allowed':
      case 'csrf_mismatch':
        return 'The request was rejected by the server security checks. Reload the page and retry.';
      default:
        return err.detail ?? err.message;
    }
  }
  if (err instanceof Error) return err.message;
  return 'An unexpected error occurred.';
}

export interface ErrorStateProps {
  title?: string;
  error: unknown;
  onRetry?: () => void;
  compact?: boolean;
  /** Show the stable API code / trace id for diagnostics (no secrets). */
  showDetails?: boolean;
}

export function ErrorState({ title, error, onRetry, compact = false, showDetails = true }: ErrorStateProps) {
  const message = apiErrorMessage(error);
  const details =
    showDetails && error instanceof ApiError
      ? [`code: ${error.code}`, error.traceId ? `trace: ${error.traceId}` : null]
          .filter(Boolean)
          .join(' · ')
      : null;

  if (compact) {
    return (
      <Alert severity="error" data-testid="error-state" action={onRetry ? <Button color="inherit" size="small" onClick={onRetry}>Retry</Button> : undefined}>
        {title ? <AlertTitle>{title}</AlertTitle> : null}
        <Typography variant="body2">{message}</Typography>
        {details ? <Typography variant="caption" sx={{ opacity: 0.8 }}>{details}</Typography> : null}
      </Alert>
    );
  }

  return (
    <Box data-testid="error-state" sx={{ py: 5, display: 'flex', justifyContent: 'center' }}>
      <Alert
        severity="error"
        sx={{ maxWidth: 640, width: '100%' }}
        action={onRetry ? <Button color="inherit" onClick={onRetry}>Retry</Button> : undefined}
      >
        <AlertTitle>{title ?? 'Could not load data'}</AlertTitle>
        <Typography variant="body2">{message}</Typography>
        {details ? <Typography variant="caption" sx={{ display: 'block', mt: 0.5, opacity: 0.8 }}>{details}</Typography> : null}
      </Alert>
    </Box>
  );
}
