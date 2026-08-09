/**
 * Alert filters + action input helpers (FRONTEND.md §8.4). Actions are
 * explicit endpoints; the UI confirms with a reason where configured.
 * Pure helpers are unit-tested in alertActions.test.ts.
 */
import type { AlertActionRequest, GetApiV1AlertsParams } from '@/api/generated/endpoints';

export interface AlertsFilters {
  status?: string;
  hostId?: string;
}

export const ALERT_STATUSES = [
  { value: '', label: 'All statuses' },
  { value: 'active', label: 'Active' },
  { value: 'acknowledged', label: 'Acknowledged' },
  { value: 'silenced', label: 'Silenced' },
  { value: 'resolved', label: 'Resolved (history)' },
] as const;

export function alertsFromSearchParams(params: URLSearchParams): AlertsFilters {
  const filters: AlertsFilters = {};
  const status = params.get('status');
  if (status === 'active' || status === 'acknowledged' || status === 'silenced' || status === 'resolved') {
    filters.status = status;
  }
  const hostId = params.get('hostId');
  if (hostId) filters.hostId = hostId;
  return filters;
}

export function alertsToSearchParams(filters: AlertsFilters): URLSearchParams {
  const params = new URLSearchParams();
  if (filters.status) params.set('status', filters.status);
  if (filters.hostId) params.set('hostId', filters.hostId);
  return params;
}

export function alertsToApiParams(filters: AlertsFilters): GetApiV1AlertsParams {
  return { status: filters.status || undefined, hostId: filters.hostId || undefined };
}

export function buildAcknowledgeRequest(reason: string | undefined): AlertActionRequest | null {
  return reason?.trim() ? { reason: reason.trim() } : null;
}

export function buildSilenceRequest(untilIso: string, reason: string | undefined): AlertActionRequest {
  return { until: untilIso, reason: reason?.trim() || undefined };
}

/** Whether an alert is actionable in the current view (resolved alerts are
 *  history and keep read-only actions). */
export function isLive(alert: { status?: string }): boolean {
  return !alert.status || ['active', 'acknowledged', 'silenced'].includes(alert.status);
}
