/**
 * Event search (FRONTEND.md §8.3): filters live in the URL, free text is
 * debounced, results use a dense virtualized table with cursor pagination,
 * and row selection opens a detail panel. Saved searches serialize the
 * normalized filter state, never rendered table state.
 */
import { useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import {
  keepPreviousData,
  useQueries,
  useQueryClient,
  type UseQueryResult,
} from '@tanstack/react-query';
import {
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  InputLabel,
  MenuItem,
  Paper,
  Popover,
  Select,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import BookmarkAdd from '@mui/icons-material/BookmarkAdd';
import BookmarkBorder from '@mui/icons-material/BookmarkBorder';
import ChevronLeft from '@mui/icons-material/ChevronLeft';
import ChevronRight from '@mui/icons-material/ChevronRight';
import Delete from '@mui/icons-material/Delete';
import Edit from '@mui/icons-material/Edit';
import PlayArrow from '@mui/icons-material/PlayArrow';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import {
  deleteApiV1SavedSearchesId,
  getApiV1Events,
  patchApiV1SavedSearchesId,
  postApiV1SavedSearches,
  useGetApiV1Hosts,
  useGetApiV1SavedSearches,
} from '@/api';
import { resourcePrefixes } from '@/api/queryKeys';
import { unwrap } from '@/api';
import { DataTable, type Column } from '@/components/DataTable/DataTable';
import { LoadingState } from '@/components/LoadingState/LoadingState';
import { EmptyState } from '@/components/EmptyState/EmptyState';
import { ErrorState } from '@/components/ErrorState/ErrorState';
import { PageHeader } from '@/components/PageHeader/PageHeader';
import { TimeDisplay } from '@/components/TimeDisplay/TimeDisplay';
import { HealthBadge } from '@/components/HealthBadge/HealthBadge';
import { ConfirmDialog } from '@/components/ConfirmDialog/ConfirmDialog';
import { formatCount, toLocalDateTimeInput } from '@/lib/format';
import type { EventDto, SavedSearchDto } from '@/api/generated/endpoints';
import {
  eventFiltersFromSearchParams,
  eventFiltersToApiParams,
  eventFiltersToSavedSearch,
  eventFiltersToSearchParams,
  EVENT_MAX_PAGE_SIZE,
  EVENT_PAGE_SIZE,
  EVENT_SORTS,
  normalizeEventFilters,
  savedSearchToEventFilters,
  type EventFilters,
} from './filters';
import { EventDetailPanel } from './EventDetailPanel';

const DEBOUNCE_MS = 300;

const saveSchema = z.object({ name: z.string().trim().min(1, 'Name is required.').max(120) });
type SaveForm = z.infer<typeof saveSchema>;

export default function LogsPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const queryClient = useQueryClient();

  // Filters are derived from the URL; typing in the free-text box updates a
  // local draft and commits to the URL after the debounce.
  const filters = useMemo(
    () => normalizeEventFilters(eventFiltersFromSearchParams(searchParams)),
    [searchParams],
  );
  const [qDraft, setQDraft] = useState(filters.q ?? '');

  // Keep the draft in sync when the URL changes from outside (e.g. a saved
  // search is applied), but never wipe in-progress typing when another filter
  // commits and the URL's q is unchanged.
  const lastCommittedQ = useRef(filters.q ?? '');
  useEffect(() => {
    const urlQ = filters.q ?? '';
    if (urlQ !== lastCommittedQ.current) {
      lastCommittedQ.current = urlQ;
      setQDraft(urlQ);
    }
  }, [filters.q]);

  useEffect(() => {
    const id = window.setTimeout(() => {
      if (qDraft.trim() === lastCommittedQ.current) return;
      lastCommittedQ.current = qDraft.trim();
      commitFilters({ q: qDraft.trim() || undefined });
    }, DEBOUNCE_MS);
    return () => window.clearTimeout(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [qDraft]);

  const hosts = useGetApiV1Hosts({ query: { select: (r) => r.data } });
  const savedSearches = useGetApiV1SavedSearches({ query: { select: (r) => r.data } });

  const [saveOpen, setSaveOpen] = useState(false);
  const [renaming, setRenaming] = useState<SavedSearchDto | null>(null);
  const [deletingSearch, setDeletingSearch] = useState<SavedSearchDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [mutationError, setMutationError] = useState<unknown>(null);
  const [savedAnchor, setSavedAnchor] = useState<HTMLElement | null>(null);
  const [selectedEvent, setSelectedEvent] = useState<EventDto | null>(null);
  const [detailOpen, setDetailOpen] = useState(false);

  const { register, handleSubmit, reset } = useForm<SaveForm>({
    resolver: zodResolver(saveSchema),
    defaultValues: { name: '' },
  });

  function commitFilters(patch: Partial<EventFilters>) {
    const next = normalizeEventFilters({ ...filters, ...patch });
    setSearchParams(eventFiltersToSearchParams(next), { replace: true });
  }

  const apiParams = eventFiltersToApiParams(filters);

  // ── Cursor pagination: one cached query per page cursor ────────────────
  const [cursors, setCursors] = useState<Array<string | undefined>>([undefined]);
  const filtersKey = JSON.stringify(apiParams);
  useEffect(() => {
    setCursors([undefined]);
  }, [filtersKey]);

  const pages = useQueries({
    queries: cursors.map((cursor, index) => ({
      queryKey: ['/api/v1/events', apiParams, { cursor: cursor ?? null, page: index }],
      queryFn: () => getApiV1Events({ ...apiParams, cursor }),
      placeholderData: keepPreviousData,
      staleTime: 30_000,
    })),
  }) as UseQueryResult<Awaited<ReturnType<typeof getApiV1Events>>, unknown>[];

  const allItems = useMemo(() => {
    const items: EventDto[] = [];
    for (const page of pages) {
      const d = page.data ? unwrap(page.data) : null;
      if (d?.items) items.push(...d.items);
    }
    return items;
  }, [pages]);

  const lastPage = pages[pages.length - 1];
  const firstPagePending = pages[0]?.isPending ?? false;
  const anyError = pages.some((p) => p.isError);
  const hasMore = Boolean(lastPage.data && unwrap(lastPage.data).hasMore);
  const nextCursor = lastPage.data ? unwrap(lastPage.data).nextCursor : undefined;
  const pageCount = cursors.length;

  const loadOlder = () => {
    if (nextCursor) setCursors((c) => [...c, nextCursor]);
  };
  const goNewer = () => {
    if (pageCount > 1) setCursors((c) => c.slice(0, -1));
  };

  const invalidateSearches = () =>
    void queryClient.invalidateQueries({ queryKey: resourcePrefixes.savedSearches });

  const saveSearch = async (values: SaveForm) => {
    setBusy(true);
    setMutationError(null);
    try {
      if (renaming) {
        await patchApiV1SavedSearchesId(renaming.id ?? '', {
          name: values.name,
          filter: renaming.filter ?? null,
          updatedAt: renaming.updatedAt ?? null,
        });
      } else {
        await postApiV1SavedSearches({
          name: values.name,
          filter: eventFiltersToSavedSearch(filters),
        });
      }
      invalidateSearches();
      setSaveOpen(false);
      setRenaming(null);
      reset();
    } catch (err) {
      setMutationError(err);
    } finally {
      setBusy(false);
    }
  };

  const deleteSearch = async () => {
    if (!deletingSearch) return;
    setBusy(true);
    setMutationError(null);
    try {
      await deleteApiV1SavedSearchesId(deletingSearch.id ?? '');
      invalidateSearches();
      setDeletingSearch(null);
    } catch (err) {
      setMutationError(err);
    } finally {
      setBusy(false);
    }
  };

  const applySearch = (saved: SavedSearchDto) => {
    const next = savedSearchToEventFilters(saved.filter);
    setSearchParams(eventFiltersToSearchParams(next), { replace: true });
    setSavedAnchor(null);
  };

  const columns: Column<EventDto>[] = [
    { id: 'time', label: 'Time', always: true, width: 190, render: (e) => <TimeDisplay time={e.time} /> },
    { id: 'host', label: 'Host', width: 150, render: (e) => e.hostName ?? e.hostId ?? e.sourceName ?? e.sourceId },
    { id: 'channel', label: 'Channel', width: 150, render: (e) => e.channel ?? '—' },
    { id: 'severity', label: 'Severity', width: 90, render: (e) => <HealthBadge state={sevState(e.severity)} size="small" label={String(e.severity ?? '')} /> },
    { id: 'eventId', label: 'Event ID', width: 90, render: (e) => String(e.eventId ?? '—') },
    {
      id: 'message',
      label: 'Message',
      render: (e) => (
        <Typography variant="body2" sx={{ whiteSpace: 'normal', overflow: 'hidden', textOverflow: 'ellipsis', display: '-webkit-box', WebkitLineClamp: 1, WebkitBoxOrient: 'vertical' }}>
          {e.message ?? ''}
        </Typography>
      ),
    },
  ];

  const firstError = pages.find((p) => p.isError);

  return (
    <Box>
      <PageHeader
        title="Event log"
        subtitle="Server-side FTS search over ingested events. Filters are kept in the URL for bookmarking."
        actions={
          <>
            <Button
              variant="outlined"
              startIcon={<BookmarkBorder />}
              onClick={(e) => setSavedAnchor(e.currentTarget)}
            >
              Saved searches
            </Button>
            <Button
              variant="contained"
              startIcon={<BookmarkAdd />}
              onClick={() => { setRenaming(null); setSaveOpen(true); }}
            >
              Save current search
            </Button>
          </>
        }
      />

      <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
        <Stack spacing={1.5}>
          <TextField
            label="Free text (message search)"
            value={qDraft}
            onChange={(e) => setQDraft(e.target.value)}
            fullWidth
            size="small"
            placeholder="e.g. disk or 'Event ID 6008'"
          />
          <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} flexWrap="wrap">
            <TextField
              label="From"
              type="datetime-local"
              size="small"
              value={toLocalDateTimeInput(filters.from)}
              onChange={(e) => commitFilters({ from: e.target.value ? new Date(e.target.value).toISOString() : undefined })}
              InputLabelProps={{ shrink: true }}
              sx={{ minWidth: 220 }}
            />
            <TextField
              label="To"
              type="datetime-local"
              size="small"
              value={toLocalDateTimeInput(filters.to)}
              onChange={(e) => commitFilters({ to: e.target.value ? new Date(e.target.value).toISOString() : undefined })}
              InputLabelProps={{ shrink: true }}
              sx={{ minWidth: 220 }}
            />
            <FormControl size="small" sx={{ minWidth: 200 }}>
              <InputLabel>Host</InputLabel>
              <Select
                label="Host"
                value={filters.hostId ?? ''}
                onChange={(e) => commitFilters({ hostId: e.target.value || undefined })}
              >
                <MenuItem value="">All hosts</MenuItem>
                {(hosts.data ?? []).map((h) => (
                  <MenuItem key={h.id} value={h.id}>{h.name}</MenuItem>
                ))}
              </Select>
            </FormControl>
            <TextField
              label="Channel"
              size="small"
              value={filters.channel ?? ''}
              onChange={(e) => commitFilters({ channel: e.target.value || undefined })}
              sx={{ minWidth: 180 }}
            />
            <FormControl size="small" sx={{ minWidth: 160 }}>
              <InputLabel>Min. severity</InputLabel>
              <Select
                label="Min. severity"
                value={String(filters.severityMin ?? '')}
                onChange={(e) =>
                  commitFilters({ severityMin: e.target.value === '' ? undefined : Number(e.target.value) })
                }
              >
                <MenuItem value="">Any</MenuItem>
                {[1, 2, 3, 4, 5].map((s) => (
                  <MenuItem key={s} value={s}>{s} — {['Critical', 'Error', 'Warning', 'Information', 'Verbose'][s - 1]}</MenuItem>
                ))}
              </Select>
            </FormControl>
            <TextField
              label="Event ID"
              size="small"
              type="number"
              value={filters.eventId ?? ''}
              onChange={(e) => commitFilters({ eventId: e.target.value ? Number(e.target.value) : undefined })}
              sx={{ minWidth: 120 }}
            />
            <FormControl size="small" sx={{ minWidth: 180 }}>
              <InputLabel>Sort</InputLabel>
              <Select
                label="Sort"
                value={filters.sort ?? 'desc'}
                onChange={(e) => commitFilters({ sort: e.target.value as EventFilters['sort'] })}
              >
                {EVENT_SORTS.map((s) => (
                  <MenuItem key={s.value} value={s.value}>{s.label}</MenuItem>
                ))}
              </Select>
            </FormControl>
            <FormControl size="small" sx={{ minWidth: 140 }}>
              <InputLabel>Page size</InputLabel>
              <Select
                label="Page size"
                value={filters.limit ?? EVENT_PAGE_SIZE}
                onChange={(e) => commitFilters({ limit: Number(e.target.value) })}
              >
                {[EVENT_PAGE_SIZE, 100, EVENT_MAX_PAGE_SIZE].map((n) => (
                  <MenuItem key={n} value={n}>{n}</MenuItem>
                ))}
              </Select>
            </FormControl>
          </Stack>
          <Typography variant="caption" color="text.secondary">
            Searches run on the server; the browser never aggregates event data.
          </Typography>
        </Stack>
      </Paper>

      {firstPagePending && pages.length === 1 ? <LoadingState label="Searching events…" /> : null}
      {anyError ? (
        <ErrorState compact error={firstError?.error} onRetry={() => { for (const p of pages) void p.refetch(); }} />
      ) : null}

      {!firstPagePending && !anyError && allItems.length === 0 ? (
        <EmptyState
          title="No events match"
          description="Widen the time range or clear filters. Ingestion happens through the agent protocol, not this page."
        />
      ) : null}

      {allItems.length > 0 ? (
        <>
          <Stack direction="row" spacing={1} alignItems="center" sx={{ mb: 1 }}>
            <Tooltip title="Newer page">
              <span>
                <Button size="small" startIcon={<ChevronLeft />} disabled={pageCount <= 1} onClick={goNewer}>
                  Newer
                </Button>
              </span>
            </Tooltip>
            <Tooltip title="Older page">
              <span>
                <Button size="small" endIcon={<ChevronRight />} disabled={!hasMore} onClick={loadOlder}>
                  Older
                </Button>
              </span>
            </Tooltip>
            <Typography variant="caption" color="text.secondary">
              {formatCount(allItems.length)} events loaded · page {pageCount}
              {hasMore ? ' · more available' : ''}
            </Typography>
            {lastPage?.isFetching ? <Typography variant="caption" color="text.secondary">refreshing…</Typography> : null}
          </Stack>
          <DataTable
            columns={columns}
            rows={allItems}
            rowKey={(e) => e.id ?? ''}
            virtualize
            maxHeight={640}
            aria-label="Event search results"
            getRowProps={(e) => ({
              onClick: () => { setSelectedEvent(e); setDetailOpen(true); },
              style: { cursor: 'pointer' },
            })}
          />
        </>
      ) : null}

      {/* ── Saved searches popover ── */}
      <Popover
        open={Boolean(savedAnchor)}
        anchorEl={savedAnchor}
        onClose={() => setSavedAnchor(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
      >
        <Box sx={{ p: 2, width: 360 }}>
          <Typography variant="overline" color="text.secondary">Saved searches</Typography>
          {savedSearches.isPending ? <LoadingState label="Loading…" /> : null}
          {savedSearches.data && savedSearches.data.length === 0 ? (
            <Typography variant="body2" color="text.secondary">No saved searches yet.</Typography>
          ) : null}
          <Stack spacing={1} sx={{ mt: 1 }}>
            {(savedSearches.data ?? []).map((s) => (
              <Stack key={s.id} direction="row" spacing={1} alignItems="center">
                <Tooltip title="Apply this search">
                  <Button size="small" variant="outlined" startIcon={<PlayArrow fontSize="small" />} onClick={() => applySearch(s)} sx={{ flexGrow: 1, justifyContent: 'flex-start', textTransform: 'none' }}>
                    {s.name}
                  </Button>
                </Tooltip>
                <Tooltip title="Rename">
                  <Button size="small" aria-label={`Rename ${s.name}`} onClick={() => { reset({ name: s.name }); setRenaming(s); setSaveOpen(true); }}>
                    <Edit fontSize="small" />
                  </Button>
                </Tooltip>
                <Tooltip title="Delete">
                  <Button size="small" color="error" aria-label={`Delete ${s.name}`} onClick={() => setDeletingSearch(s)}>
                    <Delete fontSize="small" />
                  </Button>
                </Tooltip>
              </Stack>
            ))}
          </Stack>
        </Box>
      </Popover>

      {/* ── Save / rename dialog ── */}
      <Dialog open={saveOpen} onClose={busy ? undefined : () => { setSaveOpen(false); setRenaming(null); }} maxWidth="xs" fullWidth>
        <DialogTitle>{renaming ? 'Rename saved search' : 'Save current search'}</DialogTitle>
        <form onSubmit={handleSubmit(saveSearch)} noValidate>
          <DialogContent>
            {mutationError ? <ErrorState compact error={mutationError} /> : null}
            <TextField
              {...register('name')}
              label="Name"
              fullWidth
              margin="dense"
              autoFocus
              disabled={busy}
            />
            {!renaming ? (
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
                Saves the normalized filters (time range, host, channel, severity, query text),
                not the rendered table.
              </Typography>
            ) : null}
          </DialogContent>
          <DialogActions>
            <Button onClick={() => { setSaveOpen(false); setRenaming(null); }} disabled={busy} color="inherit">Cancel</Button>
            <Button type="submit" variant="contained" disabled={busy}>{busy ? 'Saving…' : renaming ? 'Rename' : 'Save search'}</Button>
          </DialogActions>
        </form>
      </Dialog>

      <ConfirmDialog
        open={deletingSearch !== null}
        title={`Delete saved search "${deletingSearch?.name ?? ''}"?`}
        body="This only removes the saved search; it does not delete any events."
        confirmLabel="Delete"
        danger
        busy={busy}
        onConfirm={() => void deleteSearch()}
        onCancel={() => { if (!busy) { setDeletingSearch(null); setMutationError(null); } }}
      />

      <EventDetailPanel event={detailOpen ? selectedEvent : null} onClose={() => setDetailOpen(false)} />
    </Box>
  );
}

function sevState(severity: number | string | null | undefined): string {
  const n = typeof severity === 'string' ? Number(severity) : severity;
  if (n == null || Number.isNaN(n)) return 'unknown';
  if (n <= 2) return 'critical';
  if (n === 3) return 'warning';
  return 'ok';
}
