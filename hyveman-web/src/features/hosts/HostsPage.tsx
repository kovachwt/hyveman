/** Host list (FRONTEND.md §5/§8.2): client-side filters persisted in the
 *  URL, create/edit/delete, navigation to host details and logon stats. */
import { useMemo, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import {
  Box,
  Button,
  FormControl,
  IconButton,
  InputLabel,
  MenuItem,
  Paper,
  Select,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import Add from '@mui/icons-material/Add';
import Delete from '@mui/icons-material/Delete';
import Edit from '@mui/icons-material/Edit';
import Login from '@mui/icons-material/Login';
import OpenInNew from '@mui/icons-material/OpenInNew';
import {
  deleteApiV1HostsId,
  postApiV1Hosts,
  patchApiV1HostsId,
  useGetApiV1Hosts,
  useGetApiV1Sources,
} from '@/api';
import { DataTable, type Column } from '@/components/DataTable/DataTable';
import { LoadingState } from '@/components/LoadingState/LoadingState';
import { EmptyState } from '@/components/EmptyState/EmptyState';
import { ErrorState } from '@/components/ErrorState/ErrorState';
import { PageHeader } from '@/components/PageHeader/PageHeader';
import { ConfirmDialog } from '@/components/ConfirmDialog/ConfirmDialog';
import { TimeDisplay } from '@/components/TimeDisplay/TimeDisplay';
import { HostFormDialog } from './HostFormDialog';
import { buildHostInput, HOST_KINDS, type HostFormValues } from './hostForm';
import type { HostDto } from '@/api/generated/endpoints';

export default function HostsPage() {
  const queryClient = useQueryClient();
  const navigate = useNavigate();

  const hosts = useGetApiV1Hosts({ query: { select: (r) => r.data } });
  const sources = useGetApiV1Sources({ query: { select: (r) => r.data } });

  // Client-side filters (the hosts endpoint has no server-side filters),
  // persisted in the URL like the other list pages (§5: "Host list and
  // filters"). The fleet is small; the API stays authoritative for CRUD.
  const [searchParams, setSearchParams] = useSearchParams();
  const q = (searchParams.get('q') ?? '').trim().toLowerCase();
  const kind = searchParams.get('kind') ?? '';
  const enabled = searchParams.get('enabled') ?? '';

  const commitFilters = (patch: { q?: string; kind?: string; enabled?: string }) => {
    const next = new URLSearchParams(searchParams);
    for (const [key, value] of Object.entries(patch)) {
      if (value) next.set(key, value);
      else next.delete(key);
    }
    setSearchParams(next, { replace: true });
  };

  const filtersActive = q !== '' || kind !== '' || enabled !== '';

  const filtered = useMemo(() => {
    const rows = hosts.data ?? [];
    if (!filtersActive) return rows;
    return rows.filter((h) => {
      if (q) {
        const haystack = [h.name, h.sourceId, h.notes, h.idracUrl]
          .filter(Boolean)
          .join(' ')
          .toLowerCase();
        if (!haystack.includes(q)) return false;
      }
      if (kind && h.kind !== kind) return false;
      if (enabled !== '' && String(h.enabled) !== enabled) return false;
      return true;
    });
  }, [hosts.data, q, kind, enabled, filtersActive]);

  const [formOpen, setFormOpen] = useState(false);
  const [editing, setEditing] = useState<HostDto | null>(null);
  const [deleting, setDeleting] = useState<HostDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [formError, setFormError] = useState<unknown>(null);
  const [deleteError, setDeleteError] = useState<unknown>(null);

  const invalidateHost = () => {
    void queryClient.invalidateQueries({ queryKey: ['/api/v1/hosts'] });
  };

  const handleSubmit = async (values: HostFormValues) => {
    setBusy(true);
    setFormError(null);
    try {
      const input = buildHostInput(values, editing !== null, editing?.updatedAt);
      if (editing) {
        await patchApiV1HostsId(editing.id ?? '', input);
      } else {
        await postApiV1Hosts(input);
      }
      invalidateHost();
      setFormOpen(false);
    } catch (err) {
      setFormError(err);
    } finally {
      setBusy(false);
    }
  };

  const handleDelete = async () => {
    if (!deleting) return;
    setBusy(true);
    setDeleteError(null);
    try {
      await deleteApiV1HostsId(deleting.id ?? '', { confirm: true });
      invalidateHost();
      setDeleting(null);
    } catch (err) {
      setDeleteError(err);
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<HostDto>[] = [
    {
      id: 'name',
      label: 'Name',
      always: true,
      render: (h) => (
        <Stack direction="row" spacing={1} alignItems="center">
          <Typography variant="body2" sx={{ fontWeight: 600 }}>{h.name}</Typography>
          <IconButton
            size="small"
            aria-label={`Open ${h.name} details`}
            onClick={(e) => { e.stopPropagation(); navigate(`/hosts/${h.id}`); }}
          >
            <OpenInNew fontSize="inherit" />
          </IconButton>
        </Stack>
      ),
    },
    { id: 'kind', label: 'Kind', render: (h) => h.kind },
    {
      id: 'source',
      label: 'Source',
      render: (h) => (
        <Tooltip title={h.sourceId ?? 'No agent source associated'}>
          <span>{h.sourceId ?? '—'}</span>
        </Tooltip>
      ),
    },
    {
      id: 'idrac',
      label: 'iDRAC',
      render: (h) =>
        h.idracUrl ? (
          <Tooltip title={h.idracUrl}>
            <Typography variant="body2">
              {h.idracUrl.replace(/^https:\/\//, '')}
              {h.idracCredentialSet ? '' : ' (no credentials)'}
            </Typography>
          </Tooltip>
        ) : (
          <Typography variant="body2" color="text.disabled">not configured</Typography>
        ),
    },
    {
      id: 'enabled',
      label: 'Enabled',
      render: (h) => (h.enabled ? 'Yes' : 'No'),
    },
    {
      id: 'updated',
      label: 'Updated',
      render: (h) => <TimeDisplay time={h.updatedAt} variant="full" />,
    },
    {
      id: 'actions',
      label: 'Actions',
      align: 'right',
      render: (h) => (
        <Stack direction="row" spacing={0.5} justifyContent="flex-end">
          <Tooltip title="Logon stats">
            <IconButton size="small" aria-label={`Logon stats for ${h.name}`} onClick={(e) => { e.stopPropagation(); navigate(`/hosts/${h.id}/logons`); }}>
              <Login fontSize="small" />
            </IconButton>
          </Tooltip>
          <Tooltip title="Edit">
            <IconButton size="small" aria-label={`Edit ${h.name}`} onClick={(e) => { e.stopPropagation(); setEditing(h); setFormOpen(true); }}>
              <Edit fontSize="small" />
            </IconButton>
          </Tooltip>
          <Tooltip title="Delete">
            <IconButton size="small" aria-label={`Delete ${h.name}`} color="error" onClick={(e) => { e.stopPropagation(); setDeleting(h); }}>
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
        title="Hosts"
        subtitle="Hardware records, agent association, and iDRAC polling configuration."
        actions={
          <Button
            variant="contained"
            startIcon={<Add />}
            onClick={() => { setEditing(null); setFormOpen(true); }}
          >
            New host
          </Button>
        }
      />

      <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
        <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} flexWrap="wrap" alignItems="center">
          <TextField
            label="Search hosts"
            size="small"
            value={searchParams.get('q') ?? ''}
            onChange={(e) => commitFilters({ q: e.target.value })}
            placeholder="Name, source, notes, iDRAC URL"
            sx={{ minWidth: 260, flexGrow: 1 }}
          />
          <FormControl size="small" sx={{ minWidth: 170 }}>
            <InputLabel id="hosts-kind-label">Kind</InputLabel>
            <Select labelId="hosts-kind-label" id="hosts-kind" label="Kind" value={kind} onChange={(e) => commitFilters({ kind: e.target.value })}>
              <MenuItem value="">All kinds</MenuItem>
              {HOST_KINDS.map((k) => (
                <MenuItem key={k} value={k}>{k}</MenuItem>
              ))}
            </Select>
          </FormControl>
          <FormControl size="small" sx={{ minWidth: 150 }}>
            <InputLabel id="hosts-enabled-label">Enabled</InputLabel>
            <Select labelId="hosts-enabled-label" id="hosts-enabled" label="Enabled" value={enabled} onChange={(e) => commitFilters({ enabled: e.target.value })}>
              <MenuItem value="">All</MenuItem>
              <MenuItem value="true">Enabled</MenuItem>
              <MenuItem value="false">Disabled</MenuItem>
            </Select>
          </FormControl>
          {filtersActive ? (
            <Button size="small" color="inherit" onClick={() => commitFilters({ q: '', kind: '', enabled: '' })}>
              Clear filters
            </Button>
          ) : null}
        </Stack>
      </Paper>

      {hosts.isPending ? <LoadingState label="Loading hosts…" /> : null}

      {hosts.isError && !hosts.data ? (
        <ErrorState error={hosts.error} onRetry={() => void hosts.refetch()} />
      ) : null}

      {hosts.data && hosts.data.length === 0 ? (
        <EmptyState
          title="No hosts yet"
          description="Create a host and associate it with a registered agent source to start monitoring."
          action={
            <Button variant="contained" startIcon={<Add />} onClick={() => { setEditing(null); setFormOpen(true); }}>
              New host
            </Button>
          }
        />
      ) : null}

      {hosts.data && hosts.data.length > 0 && filtered.length === 0 ? (
        <EmptyState
          title="No hosts match your filters"
          description="Adjust the search text, kind, or enabled state."
          action={
            <Button variant="outlined" onClick={() => commitFilters({ q: '', kind: '', enabled: '' })}>
              Clear filters
            </Button>
          }
        />
      ) : null}

      {hosts.data && hosts.data.length > 0 && filtered.length > 0 ? (
        <>
          {filtersActive ? (
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
              Showing {filtered.length} of {hosts.data.length} hosts
            </Typography>
          ) : null}
          <DataTable
            columns={columns}
            rows={filtered}
            rowKey={(h) => h.id ?? ''}
            aria-label="Hosts"
            getRowProps={(h) => ({
              onClick: (e) => {
                // Clicks on the row's own interactive elements (edit/delete/
                // logon/open buttons) are handled by their own handlers; never
                // let them fall through to row navigation (event propagation
                // from the button is stopped too — this is defense in depth).
                const target = e.target as HTMLElement | null;
                if (target?.closest('button, a, input, select, textarea, [role="button"], [contenteditable="true"]')) {
                  return;
                }
                navigate(`/hosts/${h.id}`);
              },
              style: { cursor: 'pointer' },
            })}
          />
        </>
      ) : null}

      <HostFormDialog
        open={formOpen}
        host={editing}
        sources={sources.data ?? []}
        busy={busy}
        error={formError}
        onClose={() => { if (!busy) { setFormOpen(false); setFormError(null); } }}
        onSubmit={(v) => void handleSubmit(v)}
      />

      <ConfirmDialog
        open={deleting !== null}
        title={`Delete ${deleting?.name ?? 'host'}?`}
        body="This removes the host record and its hardware history. Events and agent data remain. This cannot be undone."
        confirmLabel="Delete host"
        danger
        busy={busy}
        onConfirm={() => void handleDelete()}
        onCancel={() => { if (!busy) { setDeleting(null); setDeleteError(null); } }}
      />
      {deleteError ? <ErrorState compact error={deleteError} /> : null}
    </Box>
  );
}
