/**
 * Event-search filter state (FRONTEND.md §8.3): filters live in the URL so
 * searches can be bookmarked/shared, and saved searches serialize the
 * normalized filter state. Pure helpers are unit-tested in filters.test.ts.
 */
import type { GetApiV1EventsParams } from '@/api/generated/endpoints';

export const EVENT_PAGE_SIZE = 50;
export const EVENT_MAX_PAGE_SIZE = 200;

export interface EventFilters {
  from?: string;
  to?: string;
  hostId?: string;
  sourceId?: string;
  channel?: string;
  severityMin?: number;
  eventId?: number;
  q?: string;
  sort?: 'time_desc' | 'time_asc' | 'severity_desc';
  limit?: number;
}

export const EVENT_SORTS = [
  { value: 'time_desc', label: 'Newest first' },
  { value: 'time_asc', label: 'Oldest first' },
  { value: 'severity_desc', label: 'Severity, then newest' },
] as const;

export function emptyEventFilters(): EventFilters {
  return { sort: 'time_desc', limit: EVENT_PAGE_SIZE };
}

/** Deserializes URL search params into normalized filters (unknown params are
 *  ignored; invalid values are dropped, never trusted). */
export function eventFiltersFromSearchParams(params: URLSearchParams): EventFilters {
  const filters: EventFilters = {};
  const from = params.get('from');
  const to = params.get('to');
  if (isIsoLike(from)) filters.from = from!;
  if (isIsoLike(to)) filters.to = to!;
  const hostId = params.get('hostId');
  if (hostId) filters.hostId = hostId;
  const sourceId = params.get('sourceId');
  if (sourceId) filters.sourceId = sourceId;
  const channel = params.get('channel');
  if (channel) filters.channel = channel;
  const severityMin = parsePositiveInt(params.get('severityMin'));
  if (severityMin !== undefined) filters.severityMin = severityMin;
  const eventId = parsePositiveInt(params.get('eventId'));
  if (eventId !== undefined) filters.eventId = eventId;
  const q = params.get('q');
  if (q) filters.q = q;
  const sort = params.get('sort');
  if (sort === 'time_asc' || sort === 'severity_desc') filters.sort = sort;
  else filters.sort = 'time_desc';
  const limit = parsePositiveInt(params.get('limit'));
  if (limit !== undefined) filters.limit = clampLimit(limit);
  return filters;
}

/** Serializes normalized filters to URL search params (empty filters are
 *  omitted so URLs stay clean and shareable). */
export function eventFiltersToSearchParams(filters: EventFilters): URLSearchParams {
  const params = new URLSearchParams();
  if (filters.from) params.set('from', filters.from);
  if (filters.to) params.set('to', filters.to);
  if (filters.hostId) params.set('hostId', filters.hostId);
  if (filters.sourceId) params.set('sourceId', filters.sourceId);
  if (filters.channel) params.set('channel', filters.channel);
  if (filters.severityMin !== undefined) params.set('severityMin', String(filters.severityMin));
  if (filters.eventId !== undefined) params.set('eventId', String(filters.eventId));
  if (filters.q) params.set('q', filters.q);
  if (filters.sort) params.set('sort', filters.sort);
  if (filters.limit !== undefined && filters.limit !== EVENT_PAGE_SIZE) {
    params.set('limit', String(filters.limit));
  }
  return params;
}

/** Normalizes a filter set: valid range, clamped limit, defaults. Returns a
 *  new object; never mutates the input. */
export function normalizeEventFilters(filters: EventFilters): EventFilters {
  const out: EventFilters = { sort: filters.sort ?? 'time_desc', limit: clampLimit(filters.limit ?? EVENT_PAGE_SIZE) };
  if (isIsoLike(filters.from) && isIsoLike(filters.to) && filters.to! < filters.from!) {
    out.from = filters.to;
    out.to = filters.from;
  } else {
    if (isIsoLike(filters.from)) out.from = filters.from;
    if (isIsoLike(filters.to)) out.to = filters.to;
  }
  if (filters.hostId) out.hostId = filters.hostId;
  if (filters.sourceId) out.sourceId = filters.sourceId;
  if (filters.channel) out.channel = filters.channel;
  if (filters.severityMin !== undefined) {
    const s = clampSeverity(filters.severityMin);
    if (s !== undefined) out.severityMin = s;
  }
  if (filters.eventId !== undefined && Number.isInteger(filters.eventId) && filters.eventId > 0) {
    out.eventId = filters.eventId;
  }
  if (filters.q?.trim()) out.q = filters.q.trim();
  return out;
}

export function clampLimit(limit: number): number {
  if (!Number.isFinite(limit)) return EVENT_PAGE_SIZE;
  return Math.min(EVENT_MAX_PAGE_SIZE, Math.max(1, Math.round(limit)));
}

export function clampSeverity(severity: number): number | undefined {
  if (!Number.isInteger(severity)) return undefined;
  if (severity >= 1 && severity <= 7) return severity;
  return undefined;
}

function parsePositiveInt(value: string | null): number | undefined {
  if (!value) return undefined;
  const n = Number(value);
  return Number.isInteger(n) && n > 0 ? n : undefined;
}

function isIsoLike(value: string | null | undefined): value is string {
  if (!value) return false;
  const d = new Date(value);
  return !Number.isNaN(d.getTime());
}

/** Builds the API query params object (Orval contract) from filters. */
export function eventFiltersToApiParams(filters: EventFilters): GetApiV1EventsParams {
  const n = normalizeEventFilters(filters);
  return {
    from: n.from,
    to: n.to,
    hostId: n.hostId,
    sourceId: n.sourceId,
    channel: n.channel,
    severityMin: n.severityMin,
    eventId: n.eventId,
    q: n.q,
    limit: n.limit,
    sort: n.sort,
  };
}

/** Saved searches serialize the normalized filter state (§8.3): the same keys
 *  as the URL, with primitive JSON values. */
export function eventFiltersToSavedSearch(filters: EventFilters): Record<string, unknown> {
  const params = eventFiltersToSearchParams(normalizeEventFilters(filters));
  const out: Record<string, unknown> = {};
  for (const [key, value] of params.entries()) {
    if (key === 'severityMin' || key === 'eventId' || key === 'limit') out[key] = Number(value);
    else out[key] = value;
  }
  return out;
}

export function savedSearchToEventFilters(filter: Record<string, unknown> | undefined | null): EventFilters {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(filter ?? {})) {
    if (value == null) continue;
    if (typeof value === 'number') params.set(key, String(value));
    else if (typeof value === 'string') params.set(key, value);
    else if (typeof value === 'boolean') params.set(key, value ? 'true' : 'false');
    // Arrays/objects from the API are not part of the event filter contract.
  }
  return normalizeEventFilters(eventFiltersFromSearchParams(params));
}
