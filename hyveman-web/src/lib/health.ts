/**
 * Health-state vocabulary (FRONTEND.md §9): states must never rely on color
 * alone — every badge pairs a color accent with a text label and an icon.
 * Pure helpers here are unit-tested in lib/health.test.ts.
 */
export type HealthState = 'ok' | 'warning' | 'critical' | 'unknown' | 'stale';

export interface HealthVisual {
  state: HealthState;
  label: string;
}

/** Normalizes API state strings (rollups, components, agents) to the visual
 *  vocabulary. Unknown inputs map to "unknown" — never to "ok". */
export function normalizeHealthState(state: string | null | undefined): HealthState {
  switch ((state ?? '').toLowerCase()) {
    case 'ok':
    case 'healthy':
    case 'normal':
    case 'good':
    case 'online':
      return 'ok';
    case 'warning':
    case 'degraded':
      return 'warning';
    case 'critical':
    case 'error':
    case 'failed':
    case 'offline':
    case 'silent':
      return 'critical';
    case 'stale':
      return 'stale';
    default:
      return 'unknown';
  }
}

export function healthLabel(state: HealthState | string | null | undefined): string {
  switch (normalizeHealthState(state)) {
    case 'ok':
      return 'OK';
    case 'warning':
      return 'Warning';
    case 'critical':
      return 'Critical';
    case 'stale':
      return 'Stale';
    default:
      return 'Unknown';
  }
}

/** Agent status is presented separately from component health: "silent" is a
 *  distinct critical-looking state, and "unknown" means no data at all. */
export function agentStatusLabel(status: string | null | undefined): string {
  switch ((status ?? 'unknown').toLowerCase()) {
    case 'online':
      return 'Agent online';
    case 'silent':
      return 'Agent silent';
    default:
      return 'Agent unknown';
  }
}

/** Severity used by alerts and events (critical/warning/info/unknown). */
export type Severity = 'critical' | 'warning' | 'info' | 'unknown';

export function normalizeSeverity(severity: string | null | undefined): Severity {
  switch ((severity ?? '').toLowerCase()) {
    case 'critical':
    case 'error':
      return 'critical';
    case 'warning':
      return 'warning';
    case 'info':
    case 'ok':
    case 'informational':
      return 'info';
    default:
      return 'unknown';
  }
}

export function severityLabel(severity: string | null | undefined): string {
  const s = normalizeSeverity(severity);
  return s === 'unknown' ? 'Unknown' : s[0]!.toUpperCase() + s.slice(1);
}

export interface HealthPalette {
  ok: string;
  warning: string;
  critical: string;
  neutral: string;
}

/** WCAG AA-conscious accents per mode; the badge always renders the text label
 *  next to the color, so the color is a reinforcement, not the only signal. */
export function healthPalette(mode: 'light' | 'dark'): HealthPalette {
  return mode === 'dark'
    ? { ok: '#66bb6a', warning: '#ffb74d', critical: '#ef5350', neutral: '#9e9e9e' }
    : { ok: '#1b5e20', warning: '#b45309', critical: '#b71c1c', neutral: '#616161' };
}

export function stateColor(state: HealthState, palette: HealthPalette): string {
  switch (state) {
    case 'ok':
      return palette.ok;
    case 'warning':
      return palette.warning;
    case 'critical':
      return palette.critical;
    default:
      return palette.neutral;
  }
}
