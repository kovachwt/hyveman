import { describe, expect, it } from 'vitest';
import {
  alertsFromSearchParams,
  alertsToApiParams,
  alertsToSearchParams,
  buildAcknowledgeRequest,
  buildSilenceRequest,
  isLive,
} from './alertActions';

describe('alert filters', () => {
  it('round-trips via URL search params', () => {
    const filters = { status: 'silenced', hostId: 'hst_1' };
    expect(alertsFromSearchParams(alertsToSearchParams(filters))).toEqual(filters);
  });

  it('rejects unknown statuses', () => {
    expect(alertsFromSearchParams(new URLSearchParams('status=bogus')).status).toBeUndefined();
  });

  it('maps to API params', () => {
    expect(alertsToApiParams({ status: 'active', hostId: 'h' })).toEqual({ status: 'active', hostId: 'h' });
    expect(alertsToApiParams({})).toEqual({});
  });
});

describe('action input builders', () => {
  it('acknowledge request carries the reason (or null body)', () => {
    expect(buildAcknowledgeRequest('  replacing disk  ')).toEqual({ reason: 'replacing disk' });
    expect(buildAcknowledgeRequest('')).toBeNull();
    expect(buildAcknowledgeRequest(undefined)).toBeNull();
  });

  it('silence request always carries an until timestamp', () => {
    const req = buildSilenceRequest('2025-08-10T00:00:00Z', 'patch window');
    expect(req.until).toBe('2025-08-10T00:00:00Z');
    expect(req.reason).toBe('patch window');
    expect(buildSilenceRequest('2025-08-10T00:00:00Z', undefined).reason).toBeUndefined();
  });
});

describe('isLive', () => {
  it('treats resolved alerts as read-only history', () => {
    expect(isLive({ status: 'active' })).toBe(true);
    expect(isLive({ status: 'acknowledged' })).toBe(true);
    expect(isLive({ status: 'silenced' })).toBe(true);
    expect(isLive({ status: 'resolved' })).toBe(false);
    expect(isLive({})).toBe(true);
  });
});
