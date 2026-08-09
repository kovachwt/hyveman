/** Alert rules CRUD (FRONTEND.md §8.4). */
import { useEffect, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useForm, Controller } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import {
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  FormControlLabel,
  IconButton,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  Switch,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import Add from '@mui/icons-material/Add';
import Delete from '@mui/icons-material/Delete';
import Edit from '@mui/icons-material/Edit';
import {
  deleteApiV1RulesId,
  patchApiV1RulesId,
  postApiV1Rules,
  useGetApiV1NotificationChannels,
  useGetApiV1Rules,
} from '@/api';
import { resourcePrefixes } from '@/api/queryKeys';
import { DataTable, type Column } from '@/components/DataTable/DataTable';
import { LoadingState } from '@/components/LoadingState/LoadingState';
import { EmptyState } from '@/components/EmptyState/EmptyState';
import { ErrorState, apiErrorMessage } from '@/components/ErrorState/ErrorState';
import { PageHeader } from '@/components/PageHeader/PageHeader';
import { ConfirmDialog } from '@/components/ConfirmDialog/ConfirmDialog';
import { TimeDisplay } from '@/components/TimeDisplay/TimeDisplay';
import type { RuleDto } from '@/api/generated/endpoints';
import {
  buildRuleInput,
  COMPARATORS,
  COMPONENT_TYPES,
  emptyRuleForm,
  HEALTH_STATES,
  RULE_SEVERITIES,
  RULE_TYPES,
  ruleFormSchema,
  ruleSummary,
  ruleToForm,
  SOURCE_KINDS,
  type RuleFormValues,
} from './ruleForm';

export default function RulesPage() {
  const queryClient = useQueryClient();
  const rules = useGetApiV1Rules({ query: { select: (r) => r.data } });
  const channels = useGetApiV1NotificationChannels({ query: { select: (r) => r.data } });

  const [formOpen, setFormOpen] = useState(false);
  const [editing, setEditing] = useState<RuleDto | null>(null);
  const [deleting, setDeleting] = useState<RuleDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const { control, handleSubmit, reset, watch } = useForm<RuleFormValues>({
    resolver: zodResolver(ruleFormSchema),
    defaultValues: emptyRuleForm(),
    mode: 'onTouched',
  });
  const type = watch('type');

  useEffect(() => {
    if (formOpen) reset(editing ? ruleToForm(editing) : emptyRuleForm());
  }, [formOpen, editing, reset]);

  const invalidate = () => void queryClient.invalidateQueries({ queryKey: resourcePrefixes.rules });

  const submit = async (values: RuleFormValues) => {
    setBusy(true);
    setError(null);
    try {
      const input = buildRuleInput(values, editing !== null, editing?.updatedAt);
      if (editing) await patchApiV1RulesId(editing.id ?? '', input);
      else await postApiV1Rules(input);
      invalidate();
      setFormOpen(false);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  const doDelete = async () => {
    if (!deleting) return;
    setBusy(true);
    setError(null);
    try {
      await deleteApiV1RulesId(deleting.id ?? '', { confirm: true });
      invalidate();
      setDeleting(null);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<RuleDto>[] = [
    {
      id: 'name',
      label: 'Rule',
      always: true,
      render: (r) => (
        <Stack>
          <Typography variant="body2" sx={{ fontWeight: 600 }}>{r.name}</Typography>
          <Typography variant="caption" color="text.secondary">{ruleSummary(r)}</Typography>
        </Stack>
      ),
    },
    { id: 'type', label: 'Type', render: (r) => <Chip label={r.type} size="small" variant="outlined" /> },
    { id: 'severity', label: 'Severity', render: (r) => r.severity },
    {
      id: 'cooldown',
      label: 'Cooldown',
      align: 'right',
      render: (r) => `${r.cooldownS}s`,
    },
    { id: 'enabled', label: 'Enabled', render: (r) => (r.enabled ? 'Yes' : 'No') },
    {
      id: 'channels',
      label: 'Channels',
      render: (r) =>
        (r.channelIds ?? []).length > 0 ? (
          <Stack direction="row" spacing={0.5}>
            {(r.channelIds ?? []).map((id) => (
              <Chip key={id} size="small" label={channels.data?.find((c) => c.id === id)?.name ?? id} variant="outlined" />
            ))}
          </Stack>
        ) : (
          '—'
        ),
    },
    { id: 'updated', label: 'Updated', render: (r) => <TimeDisplay time={r.updatedAt} variant="full" /> },
    {
      id: 'actions',
      label: 'Actions',
      align: 'right',
      render: (r) => (
        <Stack direction="row" spacing={0.25} justifyContent="flex-end">
          <Tooltip title="Edit rule">
            <IconButton size="small" aria-label={`Edit rule ${r.name}`} onClick={() => { setEditing(r); setFormOpen(true); }}>
              <Edit fontSize="small" />
            </IconButton>
          </Tooltip>
          <Tooltip title="Delete rule">
            <IconButton size="small" color="error" aria-label={`Delete rule ${r.name}`} onClick={() => setDeleting(r)}>
              <Delete fontSize="small" />
            </IconButton>
          </Tooltip>
        </Stack>
      ),
    },
  ];

  return (
    <Box>
      <PageHeader
        title="Alert rules"
        subtitle="Health, event, heartbeat, and threshold rules evaluated server-side."
        actions={
          <Button variant="contained" startIcon={<Add />} onClick={() => { setEditing(null); setFormOpen(true); }}>
            New rule
          </Button>
        }
      />

      {rules.isPending ? <LoadingState label="Loading rules…" /> : null}
      {rules.isError && !rules.data ? <ErrorState error={rules.error} onRetry={() => void rules.refetch()} /> : null}
      {rules.data && rules.data.length === 0 ? (
        <EmptyState
          title="No rules yet"
          description="Create rules to raise alerts on hardware health, events, missing heartbeats, and metric thresholds."
          action={<Button variant="contained" startIcon={<Add />} onClick={() => { setEditing(null); setFormOpen(true); }}>New rule</Button>}
        />
      ) : null}
      {rules.data && rules.data.length > 0 ? (
        <DataTable columns={columns} rows={rules.data} rowKey={(r) => r.id ?? ''} maxHeight={560} aria-label="Alert rules" getRowProps={() => ({ style: { cursor: 'default' } })} />
      ) : null}

      <Dialog open={formOpen} onClose={busy ? undefined : () => setFormOpen(false)} maxWidth="md" fullWidth>
        <DialogTitle>{editing ? `Edit rule: ${editing.name}` : 'New rule'}</DialogTitle>
        <form onSubmit={handleSubmit(submit)} noValidate>
          <DialogContent>
            {error ? <ErrorState compact error={error} title={apiErrorMessage(error)} /> : null}

            <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5}>
              <Controller
                name="name"
                control={control}
                render={({ field, fieldState }) => (
                  <TextField {...field} label="Name *" fullWidth margin="dense" error={Boolean(fieldState.error)} helperText={fieldState.error?.message} disabled={busy} />
                )}
              />
              <Controller
                name="type"
                control={control}
                render={({ field, fieldState }) => (
                  <TextField {...field} select label="Type *" fullWidth margin="dense" error={Boolean(fieldState.error)} helperText={fieldState.error?.message} disabled={busy} sx={{ minWidth: 180 }}>
                    {RULE_TYPES.map((t) => (
                      <MenuItem key={t} value={t}>{t}</MenuItem>
                    ))}
                  </TextField>
                )}
              />
              <Controller
                name="severity"
                control={control}
                render={({ field }) => (
                  <TextField {...field} select label="Severity" fullWidth margin="dense" disabled={busy} sx={{ minWidth: 140 }}>
                    {RULE_SEVERITIES.map((s) => (
                      <MenuItem key={s} value={s}>{s}</MenuItem>
                    ))}
                  </TextField>
                )}
              />
              <Controller
                name="cooldownS"
                control={control}
                render={({ field, fieldState }) => (
                  <TextField
                    {...field}
                    label="Cooldown (s)"
                    type="number"
                    fullWidth
                    margin="dense"
                    onChange={(e) => field.onChange(e.target.value === '' ? 0 : Number(e.target.value))}
                    error={Boolean(fieldState.error)}
                    helperText={fieldState.error?.message}
                    disabled={busy}
                    sx={{ minWidth: 130 }}
                  />
                )}
              />
            </Stack>

            <Controller
              name="sourceKinds"
              control={control}
              render={({ field }) => (
                <FormControl fullWidth margin="dense">
                  <InputLabel>Source kinds (optional scope)</InputLabel>
                  <Select
                    label="Source kinds (optional scope)"
                    multiple
                    value={field.value}
                    onChange={(e) => field.onChange(e.target.value)}
                    disabled={busy}
                    renderValue={(selected) => (
                      <Stack direction="row" spacing={0.5} flexWrap="wrap">
                        {(selected as string[]).map((s) => (
                          <Chip key={s} label={s} size="small" />
                        ))}
                      </Stack>
                    )}
                  >
                    {SOURCE_KINDS.map((k) => (
                      <MenuItem key={k} value={k}>{k}</MenuItem>
                    ))}
                  </Select>
                </FormControl>
              )}
            />

            <Controller
              name="channelIds"
              control={control}
              render={({ field }) => (
                <FormControl fullWidth margin="dense">
                  <InputLabel>Notification channels</InputLabel>
                  <Select
                    label="Notification channels"
                    multiple
                    value={field.value}
                    onChange={(e) => field.onChange(e.target.value)}
                    disabled={busy}
                    renderValue={(selected) => (
                      <Stack direction="row" spacing={0.5} flexWrap="wrap">
                        {(selected as string[]).map((id) => (
                          <Chip key={id} label={channels.data?.find((c) => c.id === id)?.name ?? id} size="small" />
                        ))}
                      </Stack>
                    )}
                  >
                    {(channels.data ?? []).map((c) => (
                      <MenuItem key={c.id} value={c.id}>{c.name} ({c.kind})</MenuItem>
                    ))}
                  </Select>
                </FormControl>
              )}
            />

            {type === 'health' ? (
              <Stack spacing={1} sx={{ mt: 1 }}>
                <Controller
                  name="componentTypes"
                  control={control}
                  render={({ field, fieldState }) => (
                    <FormControl fullWidth>
                      <InputLabel>Component types *</InputLabel>
                      <Select
                        label="Component types *"
                        multiple
                        value={field.value}
                        onChange={(e) => field.onChange(e.target.value)}
                        disabled={busy}
                        error={Boolean(fieldState.error)}
                        renderValue={(selected) => (
                          <Stack direction="row" spacing={0.5} flexWrap="wrap">
                            {(selected as string[]).map((s) => <Chip key={s} label={s} size="small" />)}
                          </Stack>
                        )}
                      >
                        {COMPONENT_TYPES.map((t) => (
                          <MenuItem key={t} value={t}>{t}</MenuItem>
                        ))}
                      </Select>
                    </FormControl>
                  )}
                />
                <Controller
                  name="states"
                  control={control}
                  render={({ field }) => (
                    <FormControl fullWidth>
                      <InputLabel>Health states</InputLabel>
                      <Select
                        label="Health states"
                        multiple
                        value={field.value}
                        onChange={(e) => field.onChange(e.target.value)}
                        disabled={busy}
                        renderValue={(selected) => (
                          <Stack direction="row" spacing={0.5} flexWrap="wrap">
                            {(selected as string[]).map((s) => <Chip key={s} label={s} size="small" />)}
                          </Stack>
                        )}
                      >
                        {HEALTH_STATES.map((s) => (
                          <MenuItem key={s} value={s}>{s}</MenuItem>
                        ))}
                      </Select>
                    </FormControl>
                  )}
                />
                <Controller
                  name="includeRollup"
                  control={control}
                  render={({ field }) => (
                    <FormControlLabel
                      control={<Switch checked={field.value} onChange={(e) => field.onChange(e.target.checked)} disabled={busy} />}
                      label="Include overall rollup state"
                    />
                  )}
                />
              </Stack>
            ) : null}

            {type === 'event' ? (
              <Stack spacing={1} sx={{ mt: 1 }}>
                <Controller
                  name="channel"
                  control={control}
                  render={({ field, fieldState }) => (
                    <TextField {...field} label="Channel (e.g. System, Security)" fullWidth margin="dense" error={Boolean(fieldState.error)} helperText={fieldState.error?.message} disabled={busy} />
                  )}
                />
                <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5}>
                  <Controller
                    name="eventIds"
                    control={control}
                    render={({ field }) => (
                      <TextField {...field} label="Event IDs (comma separated)" fullWidth margin="dense" disabled={busy} placeholder="4624, 4625" />
                    )}
                  />
                  <Controller
                    name="severityMin"
                    control={control}
                    render={({ field }) => (
                      <TextField
                        {...field}
                        label="Minimum severity"
                        type="number"
                        fullWidth
                        margin="dense"
                        onChange={(e) => field.onChange(e.target.value === '' ? '' : Number(e.target.value))}
                        disabled={busy}
                        sx={{ minWidth: 160 }}
                      />
                    )}
                  />
                </Stack>
                <Controller
                  name="messagePattern"
                  control={control}
                  render={({ field }) => (
                    <TextField {...field} label="Message pattern (regex)" fullWidth margin="dense" disabled={busy} placeholder="disk|volume" />
                  )}
                />
              </Stack>
            ) : null}

            {type === 'heartbeat' ? (
              <Controller
                name="silenceAfterS"
                control={control}
                render={({ field, fieldState }) => (
                  <TextField
                    {...field}
                    label="Silence after (seconds)"
                    type="number"
                    fullWidth
                    margin="dense"
                    onChange={(e) => field.onChange(e.target.value === '' ? 0 : Number(e.target.value))}
                    error={Boolean(fieldState.error)}
                    helperText={fieldState.error?.message ?? 'Fires when a matching source has not sent a heartbeat for this long.'}
                    disabled={busy}
                  />
                )}
              />
            ) : null}

            {type === 'threshold' ? (
              <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} sx={{ mt: 1 }}>
                <Controller
                  name="metric"
                  control={control}
                  render={({ field, fieldState }) => (
                    <TextField {...field} label="Metric (e.g. temperature_max_c, power_watts)" fullWidth margin="dense" error={Boolean(fieldState.error)} helperText={fieldState.error?.message} disabled={busy} />
                  )}
                />
                <Controller
                  name="comparator"
                  control={control}
                  render={({ field }) => (
                    <TextField {...field} select label="Comparator" fullWidth margin="dense" disabled={busy} sx={{ minWidth: 130 }}>
                      {COMPARATORS.map((c) => (
                        <MenuItem key={c} value={c}>{c}</MenuItem>
                      ))}
                    </TextField>
                  )}
                />
                <Controller
                  name="value"
                  control={control}
                  render={({ field, fieldState }) => (
                    <TextField
                      {...field}
                      label="Value"
                      type="number"
                      fullWidth
                      margin="dense"
                      onChange={(e) => field.onChange(e.target.value === '' ? '' : Number(e.target.value))}
                      error={Boolean(fieldState.error)}
                      helperText={fieldState.error?.message}
                      disabled={busy}
                      sx={{ minWidth: 130 }}
                    />
                  )}
                />
              </Stack>
            ) : null}
          </DialogContent>
          <DialogActions>
            <Button onClick={() => setFormOpen(false)} disabled={busy} color="inherit">Cancel</Button>
            <Button type="submit" variant="contained" disabled={busy}>{busy ? 'Saving…' : editing ? 'Save changes' : 'Create rule'}</Button>
          </DialogActions>
        </form>
      </Dialog>

      <ConfirmDialog
        open={deleting !== null}
        title={`Delete rule "${deleting?.name ?? ''}"?`}
        body="The rule stops evaluating immediately. Existing alerts are kept."
        confirmLabel="Delete rule"
        danger
        busy={busy}
        onConfirm={() => void doDelete()}
        onCancel={() => { if (!busy) { setDeleting(null); setError(null); } }}
      />
    </Box>
  );
}
