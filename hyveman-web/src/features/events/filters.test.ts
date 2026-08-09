import { describe, expect, it } from 'vitest';
import {
  clampLimit,
  clampSeverity,
  emptyEventFilters,
  eventFiltersFromSearchParams,
  eventFiltersToApiParams,
  eventFiltersToSavedSearch,
  eventFiltersToSearchParams,
  EVENT_MAX_PAGE_SIZE,
  normalizeEventFilters,
  savedSearchToEventFilters,
} from './filters';

const ISO_FROM = '2025-08-01T00:00:00Z';
const ISO_TO = '2025-08-09T23:59:59Z';

describe('URL serialization/deserialization', () => {
  it('round-trips a full filter set', () => {
    const filters = {
      from: ISO_FROM,
      to: ISO_TO,
      hostId: 'hst_1',
      sourceId: 'src_2',
      channel: 'System',
      severityMin: 3,
      eventId: 6008,
      q: 'disk failure',
      sort: 'time_asc' as const,
      limit: 100,
    };
    const params = eventFiltersToSearchParams(filters);
    expect(eventFiltersFromSearchParams(params)).toEqual(filters);
  });

  it('omits empty filters so URLs stay clean and shareable', () => {
    const params = eventFiltersToSearchParams(normalizeEventFilters(emptyEventFilters()));
    expect([...params.entries()]).toEqual([['sort', 'time_desc']]);
  });

  it('ignores unknown and invalid params, never trusting them', () => {
    const params = new URLSearchParams('from=garbage&severityMin=abc&eventId=-5&q=&evil=1&sort=random');
    const filters = eventFiltersFromSearchParams(params);
    expect(filters.from).toBeUndefined();
    expect(filters.severityMin).toBeUndefined();
    expect(filters.eventId).toBeUndefined();
    expect(filters.q).toBeUndefined();
    expect(filters.sort).toBe('time_desc');
    expect('evil' in filters).toBe(false);
  });
});

describe('normalization', () => {
  it('clamps the page size to the API cap (200)', () => {
    expect(clampLimit(50)).toBe(50);
    expect(clampLimit(200)).toBe(200);
    expect(clampLimit(9999)).toBe(EVENT_MAX_PAGE_SIZE);
    expect(clampLimit(0)).toBe(1);
    expect(clampLimit(NaN)).toBe(50);
  });

  it('clamps severity to the valid range', () => {
    expect(clampSeverity(1)).toBe(1);
    expect(clampSeverity(7)).toBe(7);
    expect(clampSeverity(9)).toBeUndefined();
    expect(clampSeverity(0)).toBeUndefined();
  });

  it('swaps an inverted time range instead of querying nonsense', () => {
    const n = normalizeEventFilters({ from: ISO_TO, to: ISO_FROM });
    expect(n.from).toBe(ISO_FROM);
    expect(n.to).toBe(ISO_TO);
  });

  it('trims free text and drops blanks', () => {
    expect(normalizeEventFilters({ q: '  disk  ' }).q).toBe('disk');
    expect(normalizeEventFilters({ q: '   ' }).q).toBeUndefined();
  });

  it('never mutates the input', () => {
    const input = { from: ISO_TO, to: ISO_FROM };
    normalizeEventFilters(input);
    expect(input).toEqual({ from: ISO_TO, to: ISO_FROM });
  });
});

describe('API params', () => {
  it('passes through the normalized contract', () => {
    expect(eventFiltersToApiParams({})).toEqual({ limit: 50, sort: 'time_desc' });
    expect(eventFiltersToApiParams({ severityMin: 2, limit: 300 })).toEqual({
      severityMin: 2,
      limit: EVENT_MAX_PAGE_SIZE,
      sort: 'time_desc',
    });
  });
});

describe('saved searches', () => {
  it('serializes the normalized filter state, not table state', () => {
    const saved = eventFiltersToSavedSearch({
      from: ISO_FROM,
      to: ISO_TO,
      channel: 'Security',
      eventId: 4624,
      severityMin: 2,
      q: 'logon',
      limit: 50,
    });
    expect(saved).toEqual({
      from: ISO_FROM,
      to: ISO_TO,
      channel: 'Security',
      eventId: 4624,
      severityMin: 2,
      q: 'logon',
      sort: 'time_desc',
    });
  });

  it('restores filters from a saved search (numbers and strings)', () => {
    const filters = savedSearchToEventFilters({
      from: ISO_FROM,
      to: ISO_TO,
      hostId: 'hst_1',
      severityMin: 3,
      eventId: 6008,
      q: 'disk',
      limit: 100,
    });
    expect(filters).toEqual({
      from: ISO_FROM,
      to: ISO_TO,
      hostId: 'hst_1',
      severityMin: 3,
      eventId: 6008,
      q: 'disk',
      limit: 100,
      sort: 'time_desc',
    });
  });

  it('tolerates junk in a saved-search filter', () => {
    const filters = savedSearchToEventFilters({ severityMin: 'x', weird: { nested: true } });
    expect(filters.severityMin).toBeUndefined();
    expect(filters.sort).toBe('time_desc');
  });
});
