/**
 * Logon-stats filter state and presentation helpers (FRONTEND.md §8.7,
 * API.md §7.5). Days are UTC calendar days (yyyy-MM-dd) and are always labeled
 * as UTC. Pure helpers are unit-tested in logonStats.test.ts.
 */
import type { GetApiV1LogonStatsParams, LogonStatDto } from '@/api/generated/endpoints';

export const LOGON_DEFAULT_PAGE_SIZE = 50;
export const LOGON_MAX_PAGE_SIZE = 200;

export interface LogonStatsFilters {
  /** Inclusive UTC day range, yyyy-MM-dd. */
  from?: string;
  to?: string;
  sourceId?: string;
  /** Exact user match. */
  user?: string;
  limit?: number;
}

/** Today's UTC day as yyyy-MM-dd. */
export function utcToday(): string {
  return new Date().toISOString().slice(0, 10);
}

/** yyyy-MM-dd n days before (UTC). */
export function utcDaysAgo(days: number): string {
  const d = new Date();
  d.setUTCDate(d.getUTCDate() - days);
  return d.toISOString().slice(0, 10);
}

export function emptyLogonStatsFilters(): LogonStatsFilters {
  return { from: utcDaysAgo(30), to: utcToday(), limit: LOGON_DEFAULT_PAGE_SIZE };
}

const DAY_RE = /^\d{4}-\d{2}-\d{2}$/;

export function isUtcDay(value: string | null | undefined): value is string {
  if (!value) return false;
  if (!DAY_RE.test(value)) return false;
  const d = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === value;
}

export function logonStatsFromSearchParams(params: URLSearchParams): LogonStatsFilters {
  const filters: LogonStatsFilters = {};
  const from = params.get('from');
  const to = params.get('to');
  if (isUtcDay(from)) filters.from = from;
  if (isUtcDay(to)) filters.to = to;
  const sourceId = params.get('sourceId');
  if (sourceId) filters.sourceId = sourceId;
  const user = params.get('user');
  if (user) filters.user = user;
  const limit = Number(params.get('limit'));
  if (Number.isInteger(limit) && limit > 0) filters.limit = clampLogonLimit(limit);
  return filters;
}

export function logonStatsToSearchParams(filters: LogonStatsFilters): URLSearchParams {
  const params = new URLSearchParams();
  if (filters.from) params.set('from', filters.from);
  if (filters.to) params.set('to', filters.to);
  if (filters.sourceId) params.set('sourceId', filters.sourceId);
  if (filters.user) params.set('user', filters.user);
  if (filters.limit !== undefined && filters.limit !== LOGON_DEFAULT_PAGE_SIZE) {
    params.set('limit', String(filters.limit));
  }
  return params;
}

export function normalizeLogonStatsFilters(filters: LogonStatsFilters): LogonStatsFilters {
  const out: LogonStatsFilters = { limit: clampLogonLimit(filters.limit ?? LOGON_DEFAULT_PAGE_SIZE) };
  if (isUtcDay(filters.from) && isUtcDay(filters.to) && filters.to < filters.from) {
    out.from = filters.to;
    out.to = filters.from;
  } else {
    if (isUtcDay(filters.from)) out.from = filters.from;
    if (isUtcDay(filters.to)) out.to = filters.to;
  }
  if (filters.sourceId) out.sourceId = filters.sourceId;
  if (filters.user?.trim()) out.user = filters.user.trim();
  return out;
}

export function clampLogonLimit(limit: number): number {
  if (!Number.isFinite(limit)) return LOGON_DEFAULT_PAGE_SIZE;
  return Math.min(LOGON_MAX_PAGE_SIZE, Math.max(1, Math.round(limit)));
}

/** API params: the service treats from/to as inclusive UTC day bounds, so
 *  day strings are sent as-is (the API resolves the day boundaries). */
export function logonStatsToApiParams(filters: LogonStatsFilters): GetApiV1LogonStatsParams {
  const n = normalizeLogonStatsFilters(filters);
  return { from: n.from, to: n.to, sourceId: n.sourceId, user: n.user, limit: n.limit };
}

/** Logon type labels (API.md §7.5): 2 = interactive/console, 10 = RDP,
 *  null = lockout (4740 carries no logon type). */
export function logonTypeLabel(logonType: number | string | null | undefined): string {
  const n = typeof logonType === 'string' ? Number(logonType) : logonType;
  if (n == null) return 'Lockout';
  if (n === 2) return 'Interactive';
  if (n === 10) return 'Remote Interactive (RDP)';
  return `Type ${n}`;
}

export interface LogonTotals {
  successes: number;
  failures: number;
  lockouts: number;
}

export function logonTotals(items: LogonStatDto[]): LogonTotals {
  let successes = 0;
  let failures = 0;
  let lockouts = 0;
  for (const item of items) {
    successes += Number(item.successCount) || 0;
    failures += Number(item.failureCount) || 0;
    if (item.logonType == null) lockouts += Number(item.failureCount) || 0;
  }
  return { successes, failures, lockouts };
}

/** Per-day stacked series for the chart (UTC days). */
export function perDaySeries(items: LogonStatDto[]): {
  days: string[];
  successes: number[];
  failures: number[];
} {
  const map = new Map<string, { s: number; f: number }>();
  for (const item of items) {
    const cur = map.get(item.day ?? '') ?? { s: 0, f: 0 };
    cur.s += Number(item.successCount) || 0;
    cur.f += Number(item.failureCount) || 0;
    map.set(item.day ?? '', cur);
  }
  const days = [...map.keys()].sort();
  return {
    days,
    successes: days.map((d) => map.get(d)!.s),
    failures: days.map((d) => map.get(d)!.f),
  };
}
