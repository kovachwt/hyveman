import { describe, expect, it } from 'vitest';
import {
  buildRuleInput,
  emptyRuleForm,
  parseEventIds,
  ruleFormSchema,
  ruleFormToMatch,
  ruleSummary,
  ruleToForm,
} from './ruleForm';

const base = emptyRuleForm();

describe('event rule match transformation', () => {
  it('builds the typed match document for event rules', () => {
    const values = ruleFormSchema.parse({
      ...base,
      name: 'Security failures',
      type: 'event',
      channel: 'Security',
      eventIds: '4624, 4625',
      severityMin: 3,
      messagePattern: 'logon',
      sourceKinds: ['windows-agent'],
    });
    expect(ruleFormToMatch(values)).toEqual({
      sourceKinds: ['windows-agent'],
      channel: 'Security',
      eventIds: [4624, 4625],
      severityMin: 3,
      messagePattern: 'logon',
    });
  });

  it('requires at least one event criterion', () => {
    const values = { ...base, name: 'empty', type: 'event' as const };
    const result = ruleFormSchema.safeParse(values);
    expect(result.success).toBe(false);
  });
});

describe('health rule match transformation', () => {
  it('submits component/state selectors', () => {
    const values = ruleFormSchema.parse({
      ...base,
      name: 'Disk warning',
      type: 'health',
      componentTypes: ['disk', 'controller'],
      states: ['warning', 'critical'],
      includeRollup: false,
    });
    expect(ruleFormToMatch(values)).toEqual({
      componentTypes: ['disk', 'controller'],
      states: ['warning', 'critical'],
      includeRollup: false,
    });
  });

  it('requires component types for health rules', () => {
    const result = ruleFormSchema.safeParse({ ...base, name: 'x', type: 'health' as const, componentTypes: [] });
    expect(result.success).toBe(false);
  });
});

describe('threshold rule match transformation', () => {
  it('submits metric/comparator/value', () => {
    const values = ruleFormSchema.parse({
      ...base,
      name: 'Hot CPU',
      type: 'threshold',
      metric: 'temperature_max_c',
      comparator: 'gte',
      value: 85,
    });
    expect(ruleFormToMatch(values)).toEqual({ metric: 'temperature_max_c', comparator: 'gte', value: 85 });
  });

  it('rejects missing metric or value', () => {
    expect(ruleFormSchema.safeParse({ ...base, name: 'x', type: 'threshold' as const, metric: '', value: 90 }).success).toBe(false);
    expect(ruleFormSchema.safeParse({ ...base, name: 'x', type: 'threshold' as const, metric: 'temp', value: '' }).success).toBe(false);
  });
});

describe('heartbeat rule', () => {
  it('submits silenceAfterS', () => {
    const values = ruleFormSchema.parse({ ...base, name: 'silent', type: 'heartbeat' as const, silenceAfterS: 600 });
    expect(ruleFormToMatch(values)).toEqual({ silenceAfterS: 600 });
  });
});

describe('vm_heartbeat rule', () => {
  it('accepts an empty match document and round-trips', () => {
    const values = ruleFormSchema.parse({ ...base, name: 'VM down', type: 'vm_heartbeat' as const });
    expect(ruleFormToMatch(values)).toEqual({});
    const form = ruleToForm({
      id: 'r1',
      name: 'VM down',
      type: 'vm_heartbeat',
      severity: 'critical',
      cooldownS: 0,
      enabled: true,
      channelIds: [],
      match: {},
    } as never);
    expect(form.type).toBe('vm_heartbeat');
  });
});

describe('parseEventIds', () => {
  it('parses comma-separated positive integers', () => {
    expect(parseEventIds('4624, 4625,6008')).toEqual([4624, 4625, 6008]);
    expect(parseEventIds('')).toBeNull();
    expect(parseEventIds('abc')).toBeNull();
    expect(parseEventIds('1,-2')).toBeNull();
  });
});

describe('ruleToForm (edit round-trip)', () => {
  it('hydrates a rule back into the form, ignoring unknown fields', () => {
    const rule = {
      id: 'r1',
      name: 'Disk warning',
      type: 'health',
      severity: 'warning',
      cooldownS: 300,
      enabled: true,
      channelIds: ['c1'],
      match: {
        componentTypes: ['disk'],
        states: ['warning'],
        includeRollup: true,
        futureField: 'ignored',
      },
    };
    const form = ruleToForm(rule as never);
    expect(form.name).toBe('Disk warning');
    expect(form.type).toBe('health');
    expect(form.componentTypes).toEqual(['disk']);
    expect(form.states).toEqual(['warning']);
    expect(form.channelIds).toEqual(['c1']);
  });
});

describe('buildRuleInput', () => {
  it('includes updatedAt on edit for concurrency checks', () => {
    const values = ruleFormSchema.parse({ ...base, name: 'r', type: 'event' as const, channel: 'System' });
    const input = buildRuleInput(values, true, '2025-08-09T00:00:00Z');
    expect(input.updatedAt).toBe('2025-08-09T00:00:00Z');
    expect(buildRuleInput(values, false, 'x').updatedAt).toBeUndefined();
  });
});

describe('ruleSummary (human-readable)', () => {
  it('describes each rule type', () => {
    expect(ruleSummary({ type: 'health', match: { componentTypes: ['disk'], states: ['critical'] } })).toContain('disk');
    expect(ruleSummary({ type: 'event', match: { channel: 'Security', eventIds: [4625] } })).toContain('Security');
    expect(ruleSummary({ type: 'heartbeat', match: { silenceAfterS: 300 } })).toContain('300');
    expect(ruleSummary({ type: 'vm_heartbeat', match: {} })).toContain('prior OK heartbeat');
    expect(ruleSummary({ type: 'threshold', match: { metric: 'power_watts', comparator: 'gt', value: 500 } })).toContain('500');
  });
});
