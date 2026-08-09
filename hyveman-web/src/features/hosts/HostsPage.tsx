/** Host list (FRONTEND.md §8.2): table, create/edit/delete, navigation to
 *  host details and logon stats. */
import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import {
  Box,
  Button,
  IconButton,
  Stack,
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
import { buildHostInput, type HostFormValues } from './hostForm';
import type { HostDto } from '@/api/generated/endpoints';

export default function HostsPage() {
  const queryClient = useQueryClient();
  const navigate = useNavigate();

  const hosts = useGetApiV1Hosts({ query: { select: (r) => r.data } });
  const sources = useGetApiV1Sources({ query: { select: (r) => r.data } });

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
            onClick={() => navigate(`/hosts/${h.id}`)}
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
            <IconButton size="small" aria-label={`Logon stats for ${h.name}`} onClick={() => navigate(`/hosts/${h.id}/logons`)}>
              <Login fontSize="small" />
            </IconButton>
          </Tooltip>
          <Tooltip title="Edit">
            <IconButton size="small" aria-label={`Edit ${h.name}`} onClick={() => { setEditing(h); setFormOpen(true); }}>
              <Edit fontSize="small" />
            </IconButton>
          </Tooltip>
          <Tooltip title="Delete">
            <IconButton size="small" aria-label={`Delete ${h.name}`} color="error" onClick={() => setDeleting(h)}>
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

      {hosts.data && hosts.data.length > 0 ? (
        <DataTable
          columns={columns}
          rows={hosts.data}
          rowKey={(h) => h.id ?? ''}
          aria-label="Hosts"
          getRowProps={(h) => ({
            onClick: () => navigate(`/hosts/${h.id}`),
            style: { cursor: 'pointer' },
          })}
        />
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
