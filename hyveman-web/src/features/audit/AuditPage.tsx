/** Audit log (FRONTEND.md §8.6): filterable configuration/auth history with
 *  cursor pagination. Detail JSON renders as escaped text only. */
import { useEffect, useMemo, useState } from 'react';
import { keepPreviousData, useQueries, type UseQueryResult } from '@tanstack/react-query';
import {
  Box,
  Button,
  Paper,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import ChevronLeft from '@mui/icons-material/ChevronLeft';
import ChevronRight from '@mui/icons-material/ChevronRight';
import { getApiV1AuditLog } from '@/api';
import { unwrap } from '@/api';
import { DataTable, type Column } from '@/components/DataTable/DataTable';
import { LoadingState } from '@/components/LoadingState/LoadingState';
import { EmptyState } from '@/components/EmptyState/EmptyState';
import { ErrorState } from '@/components/ErrorState/ErrorState';
import { PageHeader } from '@/components/PageHeader/PageHeader';
import { TimeDisplay } from '@/components/TimeDisplay/TimeDisplay';
import { formatCount } from '@/lib/format';
import type { AuditEntryDto } from '@/api/generated/endpoints';

export default function AuditPage() {
  const [action, setAction] = useState('');
  const [targetKind, setTargetKind] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [applied, setApplied] = useState({ action: '', targetKind: '', from: '', to: '' });
  const [cursors, setCursors] = useState<Array<string | undefined>>([undefined]);
  const [detail, setDetail] = useState<AuditEntryDto | null>(null);

  const params = useMemo(
    () => ({
      action: applied.action || undefined,
      targetKind: applied.targetKind || undefined,
      from: applied.from ? new Date(applied.from).toISOString() : undefined,
      to: applied.to ? new Date(applied.to).toISOString() : undefined,
      limit: 50,
    }),
    [applied],
  );

  useEffect(() => {
    setCursors([undefined]);
    setDetail(null);
  }, [applied]);

  const pages = useQueries({
    queries: cursors.map((cursor, index) => ({
      queryKey: ['/api/v1/audit-log', params, { cursor: cursor ?? null, page: index }],
      queryFn: () => getApiV1AuditLog({ ...params, cursor }),
      placeholderData: keepPreviousData,
    })),
  }) as UseQueryResult<Awaited<ReturnType<typeof getApiV1AuditLog>>, unknown>[];

  const items = useMemo(() => {
    const out: AuditEntryDto[] = [];
    for (const page of pages) {
      const d = page.data ? unwrap(page.data) : null;
      if (d?.items) out.push(...d.items);
    }
    return out;
  }, [pages]);

  const lastPage = pages[pages.length - 1];
  const hasMore = Boolean(lastPage.data && unwrap(lastPage.data).hasMore);
  const nextCursor = lastPage.data ? unwrap(lastPage.data).nextCursor : undefined;
  const firstError = pages.find((p) => p.isError);

  const columns: Column<AuditEntryDto>[] = [
    { id: 'time', label: 'Time', always: true, width: 190, render: (a) => <TimeDisplay time={a.time} /> },
    { id: 'actor', label: 'Actor', width: 90, render: (a) => a.actor ?? '—' },
    { id: 'action', label: 'Action', width: 150, render: (a) => <Typography variant="body2" sx={{ fontFamily: 'monospace', fontWeight: 600 }}>{a.action}</Typography> },
    { id: 'target', label: 'Target', render: (a) => `${a.targetKind ?? ''}${a.targetId ? ` ${a.targetId}` : ''}` },
    {
      id: 'detail',
      label: 'Details',
      render: (a) =>
        a.detailJson ? (
          <Button size="small" variant="outlined" onClick={() => setDetail(a)}>
            View
          </Button>
        ) : (
          '—'
        ),
    },
  ];

  return (
    <Box>
      <PageHeader title="Audit log" subtitle="Configuration changes, alert actions, token lifecycle, and authentication ceremonies." />

      <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
        <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} flexWrap="wrap">
          <TextField label="Action contains" size="small" value={action} onChange={(e) => setAction(e.target.value)} sx={{ minWidth: 200 }} />
          <TextField label="Target kind" size="small" value={targetKind} onChange={(e) => setTargetKind(e.target.value)} sx={{ minWidth: 160 }} />
          <TextField label="From" type="datetime-local" size="small" value={from} onChange={(e) => setFrom(e.target.value)} InputLabelProps={{ shrink: true }} />
          <TextField label="To" type="datetime-local" size="small" value={to} onChange={(e) => setTo(e.target.value)} InputLabelProps={{ shrink: true }} />
          <Button variant="contained" onClick={() => setApplied({ action, targetKind, from, to })}>
            Apply filters
          </Button>
        </Stack>
      </Paper>

      {pages[0]?.isPending ? <LoadingState label="Loading audit log…" /> : null}
      {firstError ? <ErrorState compact error={firstError.error} onRetry={() => { for (const p of pages) void p.refetch(); }} /> : null}

      {!pages[0]?.isPending && !firstError && items.length === 0 ? (
        <EmptyState title="No audit entries match" description="Narrow or clear the filters." />
      ) : null}

      {items.length > 0 ? (
        <>
          <Stack direction="row" spacing={1} alignItems="center" sx={{ mb: 1 }}>
            <Tooltip title="Newer page">
              <span>
                <Button size="small" startIcon={<ChevronLeft />} disabled={cursors.length <= 1} onClick={() => setCursors((c) => c.slice(0, -1))}>
                  Newer
                </Button>
              </span>
            </Tooltip>
            <Tooltip title="Older page">
              <span>
                <Button size="small" endIcon={<ChevronRight />} disabled={!hasMore} onClick={() => { if (nextCursor) setCursors((c) => [...c, nextCursor]); }}>
                  Older
                </Button>
              </span>
            </Tooltip>
            <Typography variant="caption" color="text.secondary">
              {formatCount(items.length)} entries · page {cursors.length}
            </Typography>
          </Stack>
          <DataTable columns={columns} rows={items} rowKey={(a) => a.id ?? ''} maxHeight={560} aria-label="Audit log" getRowProps={() => ({ style: { cursor: 'default' } })} />
        </>
      ) : null}

      <AuditDetailDialog entry={detail} onClose={() => setDetail(null)} />
    </Box>
  );
}

function AuditDetailDialog({ entry, onClose }: { entry: AuditEntryDto | null; onClose: () => void }) {
  if (!entry) return null;
  let pretty = entry.detailJson ?? '';
  try {
    pretty = JSON.stringify(JSON.parse(pretty), null, 2);
  } catch {
    // Keep escaped text as-is.
  }
  return (
    <Paper
      variant="outlined"
      sx={{ position: 'fixed', right: 16, bottom: 16, maxWidth: 480, width: '90%', maxHeight: 420, overflow: 'auto', p: 2, zIndex: 1300, boxShadow: 8 }}
      role="dialog"
      aria-label="Audit entry details"
    >
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 1 }}>
        <Typography variant="subtitle1">{entry.action}</Typography>
        <Button size="small" onClick={onClose}>Close</Button>
      </Stack>
      <Typography variant="caption" color="text.secondary">
        {entry.actor ?? 'system'} · <TimeDisplay time={entry.time} variant="full" />
      </Typography>
      <Box
        component="pre"
        data-testid="audit-detail-json"
        sx={{ mt: 1, maxHeight: 280, overflow: 'auto', p: 1, borderRadius: 1, bgcolor: 'action.hover', fontSize: 12, lineHeight: 1.5, whiteSpace: 'pre-wrap', wordBreak: 'break-word', m: 0 }}
      >
        {pretty}
      </Box>
    </Paper>
  );
}
