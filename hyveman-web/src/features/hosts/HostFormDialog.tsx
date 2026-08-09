/** Host create/edit dialog (React Hook Form + Zod; API remains authoritative). */
import { useEffect } from 'react';
import { useForm, Controller } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import {
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  MenuItem,
  Switch,
  TextField,
  Typography,
} from '@mui/material';
import type { HostDto, SourceDto } from '@/api/generated/endpoints';
import { SecretField } from '@/components/SecretField/SecretField';
import { ErrorState, apiErrorMessage } from '@/components/ErrorState/ErrorState';
import {
  emptyHostForm,
  hostFormFromDto,
  hostFormSchema,
  HOST_KINDS,
  type HostFormValues,
} from './hostForm';

export interface HostFormDialogProps {
  open: boolean;
  /** When set, the dialog edits this host; otherwise it creates one. */
  host: HostDto | null;
  sources: SourceDto[];
  busy: boolean;
  error: unknown;
  onClose: () => void;
  onSubmit: (values: HostFormValues) => void;
}

export function HostFormDialog({ open, host, sources, busy, error, onClose, onSubmit }: HostFormDialogProps) {
  const edit = host !== null;
  const { control, handleSubmit, reset } = useForm<HostFormValues>({
    resolver: zodResolver(hostFormSchema),
    defaultValues: emptyHostForm(),
    mode: 'onTouched',
  });

  useEffect(() => {
    if (open) reset(host ? hostFormFromDto(host) : emptyHostForm());
  }, [open, host, reset]);

  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} maxWidth="sm" fullWidth>
      <DialogTitle>{edit ? `Edit ${host!.name}` : 'New host'}</DialogTitle>
      <form onSubmit={handleSubmit((v) => onSubmit(v))} noValidate>
        <DialogContent>
          {error ? (
            <ErrorState compact error={error} title={apiErrorMessage(error)} />
          ) : null}

          <Controller
            name="name"
            control={control}
            render={({ field, fieldState }) => (
              <TextField
                {...field}
                label="Name *"
                fullWidth
                margin="dense"
                error={Boolean(fieldState.error)}
                helperText={fieldState.error?.message}
                disabled={busy}
              />
            )}
          />
          <Controller
            name="kind"
            control={control}
            render={({ field, fieldState }) => (
              <TextField
                {...field}
                select
                label="Kind *"
                fullWidth
                margin="dense"
                error={Boolean(fieldState.error)}
                helperText={fieldState.error?.message}
                disabled={busy}
              >
                {HOST_KINDS.map((k) => (
                  <MenuItem key={k} value={k}>{k}</MenuItem>
                ))}
              </TextField>
            )}
          />
          <Controller
            name="sourceId"
            control={control}
            render={({ field }) => (
              <TextField
                {...field}
                select
                label="Associated source (optional)"
                fullWidth
                margin="dense"
                disabled={busy}
                helperText="Links the host to an agent source for OS/Hyper-V state and events."
              >
                <MenuItem value="">— none —</MenuItem>
                {sources.map((s) => (
                  <MenuItem key={s.id} value={s.id}>
                    {s.name} ({s.kind})
                  </MenuItem>
                ))}
              </TextField>
            )}
          />
          <Controller
            name="idracUrl"
            control={control}
            render={({ field, fieldState }) => (
              <TextField
                {...field}
                label="iDRAC URL"
                fullWidth
                margin="dense"
                placeholder="https://idrac.example.internal"
                error={Boolean(fieldState.error)}
                helperText={fieldState.error?.message ?? 'https:// only; no user info.'}
                disabled={busy}
              />
            )}
          />
          <Controller
            name="idracUsername"
            control={control}
            render={({ field, fieldState }) => (
              <SecretField
                {...field}
                label="iDRAC username"
                fullWidth
                margin="dense"
                editMode={edit}
                error={Boolean(fieldState.error)}
                helperText={fieldState.error?.message}
                disabled={busy}
              />
            )}
          />
          <Controller
            name="idracPassword"
            control={control}
            render={({ field, fieldState }) => (
              <SecretField
                {...field}
                label="iDRAC password"
                fullWidth
                margin="dense"
                editMode={edit}
                error={Boolean(fieldState.error)}
                helperText={fieldState.error?.message}
                disabled={busy}
              />
            )}
          />
          <Controller
            name="enabled"
            control={control}
            render={({ field }) => (
              <FormControlLabel
                control={<Switch checked={field.value} onChange={(e) => field.onChange(e.target.checked)} disabled={busy} />}
                label="Enabled (hardware polling on)"
                sx={{ mt: 1 }}
              />
            )}
          />
          <Controller
            name="notes"
            control={control}
            render={({ field, fieldState }) => (
              <TextField
                {...field}
                label="Notes"
                fullWidth
                multiline
                minRows={2}
                margin="dense"
                error={Boolean(fieldState.error)}
                helperText={fieldState.error?.message}
                disabled={busy}
              />
            )}
          />
          {edit ? (
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
              Blank iDRAC credentials keep the stored values unchanged. Entered credentials are
              never returned by the API and are cleared after saving.
            </Typography>
          ) : null}
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={busy} color="inherit">
            Cancel
          </Button>
          <Button type="submit" variant="contained" disabled={busy}>
            {busy ? 'Saving…' : edit ? 'Save changes' : 'Create host'}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
