import { describe, expect, it } from 'vitest';
import {
  clampLogonLimit,
  emptyLogonStatsFilters,
  isUtcDay,
  logonStatsFromSearchParams,
  logonStatsToApiParams,
  logonStatsToSearchParams,
  logonTotals,
  logonTypeLabel,
  normalizeLogonStatsFilters,
  perDaySeries,
  utcDaysAgo,
  utcToday,
} from './logonStats';
import type { LogonStatDto } from '@/api/generated/endpoints';

describe('UTC days', () => {
  it('recognizes yyyy-MM-dd and rejects everything else', () => {
    expect(isUtcDay('2025-08-09')).toBe(true);
    expect(isUtcDay('2025-13-01')).toBe(false);
    expect(isUtcDay('2025-08-09T00:00:00Z')).toBe(false);
    expect(isUtcDay(undefined)).toBe(false);
  });

  it('computes today and n-days-ago as UTC days', () => {
    expect(utcToday()).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    const daysAgo = utcDaysAgo(7);
    expect(daysAgo).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    // Approximately 7 days before today.
    const diff = (Date.parse(utcToday() + 'T00:00:00Z') - Date.parse(daysAgo + 'T00:00:00Z')) / 86_400_000;
    expect(Math.abs(diff - 7)).toBeLessThanOrEqual(1);
  });
});

describe('filter serialization', () => {
  it('round-trips via URL search params', () => {
    const filters = { from: '2025-08-01', to: '2025-08-09', sourceId: 'src_1', user: 'alice', limit: 100 };
    const params = logonStatsToSearchParams(filters);
    expect(logonStatsFromSearchParams(params)).toEqual(filters);
  });

  it('drops the limit param when it equals the default', () => {
    const params = logonStatsToSearchParams({ ...emptyLogonStatsFilters(), limit: 50 });
    expect(params.get('limit')).toBeNull();
  });

  it('normalizes ranges and invalid values', () => {
    const n = normalizeLogonStatsFilters({ from: '2025-08-09', to: '2025-08-01', limit: 999 });
    expect(n.from).toBe('2025-08-01');
    expect(n.to).toBe('2025-08-09');
    expect(n.limit).toBe(clampLogonLimit(999));
    expect(normalizeLogonStatsFilters({ from: 'bogus' }).from).toBeUndefined();
  });

  it('exact-match user filters are trimmed, not fuzzy', () => {
    expect(normalizeLogonStatsFilters({ user: '  alice  ' }).user).toBe('alice');
    expect(normalizeLogonStatsFilters({ user: '  ' }).user).toBeUndefined();
  });

  it('builds API params without a cursor (bounded result)', () => {
    const params = logonStatsToApiParams({ from: '2025-08-01', to: '2025-08-09', limit: 200 });
    expect(params).toEqual({ from: '2025-08-01', to: '2025-08-09', limit: 200 });
    expect('cursor' in params).toBe(false);
  });
});

describe('presentation helpers', () => {
  const rows: LogonStatDto[] = [
    { day: '2025-08-09', sourceId: 's1', user: 'alice', logonType: 2, successCount: 3, failureCount: 0 },
    { day: '2025-08-09', sourceId: 's1', user: 'alice', logonType: 10, successCount: 1, failureCount: 0 },
    { day: '2025-08-09', sourceId: 's1', user: 'bob', logonType: 2, successCount: 0, failureCount: 2 },
    { day: '2025-08-10', sourceId: 's1', user: 'carol', logonType: null, successCount: 0, failureCount: 1 },
  ];

  it('labels logon types (2 interactive, 10 RDP, null lockout)', () => {
    expect(logonTypeLabel(2)).toBe('Interactive');
    expect(logonTypeLabel(10)).toBe('Remote Interactive (RDP)');
    expect(logonTypeLabel(null)).toBe('Lockout');
    expect(logonTypeLabel('10')).toBe('Remote Interactive (RDP)');
    expect(logonTypeLabel(3)).toBe('Type 3');
  });

  it('totals successes, failures, and lockouts', () => {
    const totals = logonTotals(rows);
    expect(totals.successes).toBe(4);
    expect(totals.failures).toBe(3);
    expect(totals.lockouts).toBe(1);
  });

  it('builds per-day stacked series sorted by day', () => {
    const series = perDaySeries(rows);
    expect(series.days).toEqual(['2025-08-09', '2025-08-10']);
    expect(series.successes).toEqual([4, 0]);
    expect(series.failures).toEqual([2, 1]);
  });
});
