import { describe, expect, it } from 'vitest';
import {
  agentStatusLabel,
  healthLabel,
  healthPalette,
  normalizeHealthState,
  normalizeSeverity,
  severityLabel,
  stateColor,
} from './health';

describe('normalizeHealthState', () => {
  it('maps known wire states to the visual vocabulary', () => {
    expect(normalizeHealthState('ok')).toBe('ok');
    expect(normalizeHealthState('healthy')).toBe('ok');
    expect(normalizeHealthState('online')).toBe('ok');
    expect(normalizeHealthState('warning')).toBe('warning');
    expect(normalizeHealthState('degraded')).toBe('warning');
    expect(normalizeHealthState('critical')).toBe('critical');
    expect(normalizeHealthState('error')).toBe('critical');
    expect(normalizeHealthState('silent')).toBe('critical');
    expect(normalizeHealthState('stale')).toBe('stale');
  });

  it('never maps unknown input to ok', () => {
    expect(normalizeHealthState('bogus')).toBe('unknown');
    expect(normalizeHealthState(undefined)).toBe('unknown');
    expect(normalizeHealthState(null)).toBe('unknown');
    expect(normalizeHealthState('')).toBe('unknown');
  });

  it('is case-insensitive', () => {
    expect(normalizeHealthState('Critical')).toBe('critical');
    expect(normalizeHealthState('WARNING')).toBe('warning');
  });
});

describe('healthLabel', () => {
  it('returns text labels (never color-only)', () => {
    expect(healthLabel('ok')).toBe('OK');
    expect(healthLabel('warning')).toBe('Warning');
    expect(healthLabel('critical')).toBe('Critical');
    expect(healthLabel('stale')).toBe('Stale');
    expect(healthLabel('whatever')).toBe('Unknown');
  });
});

describe('agentStatusLabel', () => {
  it('describes agent states', () => {
    expect(agentStatusLabel('online')).toBe('Agent online');
    expect(agentStatusLabel('silent')).toBe('Agent silent');
    expect(agentStatusLabel(undefined)).toBe('Agent unknown');
  });
});

describe('severity', () => {
  it('normalizes severities', () => {
    expect(normalizeSeverity('critical')).toBe('critical');
    expect(normalizeSeverity('error')).toBe('critical');
    expect(normalizeSeverity('warning')).toBe('warning');
    expect(normalizeSeverity('info')).toBe('info');
    expect(normalizeSeverity('informational')).toBe('info');
    expect(normalizeSeverity(null)).toBe('unknown');
  });

  it('labels severities with capitals', () => {
    expect(severityLabel('critical')).toBe('Critical');
    expect(severityLabel('info')).toBe('Info');
    expect(severityLabel('bogus')).toBe('Unknown');
  });
});

describe('palette', () => {
  it('supplies distinct colors per state for both modes', () => {
    for (const mode of ['light', 'dark'] as const) {
      const p = healthPalette(mode);
      const colors = new Set([p.ok, p.warning, p.critical, p.neutral]);
      expect(colors.size).toBe(4);
      expect(stateColor('ok', p)).toBe(p.ok);
      expect(stateColor('stale', p)).toBe(p.neutral);
      expect(stateColor('unknown', p)).toBe(p.neutral);
    }
  });
});
