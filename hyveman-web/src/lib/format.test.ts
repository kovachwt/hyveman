import { describe, expect, it } from 'vitest';
import {
  formatBytes,
  formatCount,
  formatDateTime,
  formatDuration,
  formatPercent,
  formatUtcDateTime,
  relativeTime,
  toLocalDateTimeInput,
  utcDayLabel,
} from './format';

describe('formatCount', () => {
  it('formats numbers and numeric strings', () => {
    expect(formatCount(1234)).toBe('1,234');
    expect(formatCount('1234')).toBe('1,234');
    expect(formatCount(12.5, 1)).toBe('12.5');
  });

  it('renders a dash for null/NaN instead of garbage', () => {
    expect(formatCount(null)).toBe('—');
    expect(formatCount(undefined)).toBe('—');
    expect(formatCount('abc')).toBe('—');
  });
});

describe('formatPercent / formatBytes / formatDuration', () => {
  it('percent', () => {
    expect(formatPercent(42.4)).toBe('42%');
    expect(formatPercent('42.4')).toBe('42%');
    expect(formatPercent(null)).toBe('—');
  });

  it('bytes', () => {
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(4096)).toBe('4.0 KiB');
    expect(formatBytes(7340032)).toBe('7.0 MiB');
    expect(formatBytes(null)).toBe('—');
  });

  it('duration', () => {
    expect(formatDuration(90)).toBe('1m 30s');
    expect(formatDuration(7200)).toBe('2h');
    expect(formatDuration(90061)).toBe('1d 1h');
    expect(formatDuration(-1)).toBe('—');
  });
});

describe('time formatting', () => {
  it('formats local and UTC representations', () => {
    const iso = '2025-08-09T14:32:05Z';
    expect(formatDateTime(iso)).toMatch(/2025/);
    expect(formatUtcDateTime(iso)).toContain('UTC');
    expect(formatUtcDateTime(iso)).toContain('2025');
    expect(formatDateTime(undefined)).toBe('—');
  });

  it('relative times pair with absolute precision', () => {
    const now = Date.parse('2025-08-09T14:32:05Z');
    expect(relativeTime('2025-08-09T14:32:00Z', now)).toBe('5 seconds ago');
    expect(relativeTime('2025-08-09T14:30:00Z', now)).toBe('2 minutes ago');
    expect(relativeTime('2025-08-09T11:32:05Z', now)).toBe('3 hours ago');
    expect(relativeTime('2025-08-06T14:32:05Z', now)).toBe('3 days ago');
    expect(relativeTime('2024-01-01T00:00:00Z', now)).toMatch(/\d{4}/);
    expect(relativeTime(null, now)).toBe('—');
  });

  it('labels UTC calendar days explicitly', () => {
    expect(utcDayLabel('2025-08-09')).toBe('2025-08-09 (UTC)');
    expect(utcDayLabel('not-a-day')).toBe('not-a-day');
  });

  it('converts ISO instants to local datetime-local input values', () => {
    const value = toLocalDateTimeInput('2025-08-09T14:32:05Z');
    expect(value).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/);
    expect(toLocalDateTimeInput(undefined)).toBe('');
  });
});
