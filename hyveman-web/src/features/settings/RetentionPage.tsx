/** Retention settings (FRONTEND.md §8.6). */
import { useEffect, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useForm, Controller } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Alert, Box, Button, Paper, Stack, TextField, Typography } from '@mui/material';
import { patchApiV1SettingsRetention, useGetApiV1SettingsRetention } from '@/api';
import { resourcePrefixes } from '@/api/queryKeys';
import { LoadingState } from '@/components/LoadingState/LoadingState';
import { ErrorState, apiErrorMessage } from '@/components/ErrorState/ErrorState';
import { PageHeader } from '@/components/PageHeader/PageHeader';

const retentionSchema = z.object({
  eventDays: z.number().int().min(1, 'At least 1 day.').max(3650),
  metricDays: z.number().int().min(1, 'At least 1 day.').max(3650),
  snapshotDays: z.number().int().min(1, 'At least 1 day.').max(3650),
});
type RetentionForm = z.infer<typeof retentionSchema>;

export default function RetentionPage() {
  const queryClient = useQueryClient();
  const retention = useGetApiV1SettingsRetention({ query: { select: (r) => r.data } });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);

  const { control, handleSubmit, reset } = useForm<RetentionForm>({
    resolver: zodResolver(retentionSchema),
    defaultValues: { eventDays: 365, metricDays: 180, snapshotDays: 180 },
  });

  useEffect(() => {
    if (retention.data) {
      reset({
        eventDays: Number(retention.data.eventDays) || 365,
        metricDays: Number(retention.data.metricDays) || 180,
        snapshotDays: Number(retention.data.snapshotDays) || 180,
      });
    }
  }, [retention.data, reset]);

  if (retention.isPending) return <LoadingState label="Loading retention settings…" />;
  if (retention.isError && !retention.data) {
    return (
      <Box>
        <PageHeader title="Retention" />
        <ErrorState error={retention.error} onRetry={() => void retention.refetch()} />
      </Box>
    );
  }

  const submit = async (values: RetentionForm) => {
    setBusy(true);
    setError(null);
    setSaved(false);
    try {
      await patchApiV1SettingsRetention(values);
      await queryClient.invalidateQueries({ queryKey: resourcePrefixes.retention });
      setSaved(true);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Box>
      <PageHeader
        title="Retention"
        subtitle="How long events, metrics, and health snapshots are kept before the maintenance job purges them."
      />

      <Paper variant="outlined" sx={{ p: 3, maxWidth: 520 }}>
        {error ? <ErrorState compact error={error} title={apiErrorMessage(error)} /> : null}
        {saved ? <Alert severity="success" sx={{ mb: 2 }}>Retention settings saved.</Alert> : null}

        <form onSubmit={handleSubmit(submit)} noValidate>
          <Stack spacing={2}>
            <Controller
              name="eventDays"
              control={control}
              render={({ field, fieldState }) => (
                <TextField
                  {...field}
                  label="Event retention (days)"
                  type="number"
                  fullWidth
                  onChange={(e) => field.onChange(Number(e.target.value))}
                  error={Boolean(fieldState.error)}
                  helperText={fieldState.error?.message ?? 'Events older than this are purged (FTS index maintained).'}
                  disabled={busy}
                />
              )}
            />
            <Controller
              name="metricDays"
              control={control}
              render={({ field, fieldState }) => (
                <TextField
                  {...field}
                  label="Metric retention (days)"
                  type="number"
                  fullWidth
                  onChange={(e) => field.onChange(Number(e.target.value))}
                  error={Boolean(fieldState.error)}
                  helperText={fieldState.error?.message}
                  disabled={busy}
                />
              )}
            />
            <Controller
              name="snapshotDays"
              control={control}
              render={({ field, fieldState }) => (
                <TextField
                  {...field}
                  label="Health snapshot retention (days)"
                  type="number"
                  fullWidth
                  onChange={(e) => field.onChange(Number(e.target.value))}
                  error={Boolean(fieldState.error)}
                  helperText={fieldState.error?.message}
                  disabled={busy}
                />
              )}
            />
            <Button type="submit" variant="contained" disabled={busy} sx={{ alignSelf: 'flex-start' }}>
              {busy ? 'Saving…' : 'Save settings'}
            </Button>
          </Stack>
        </form>
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 2 }}>
          Backups follow a separate ladder (7 daily / 4 weekly / 12 monthly snapshots) and are not
          affected by these settings.
        </Typography>
      </Paper>
    </Box>
  );
}
