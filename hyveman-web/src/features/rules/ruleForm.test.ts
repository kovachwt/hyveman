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

describe('vm_replication rule', () => {
  it('submits the default warning/critical health selection', () => {
    const values = ruleFormSchema.parse({ ...base, name: 'repl', type: 'vm_replication' as const });
    expect(values.replicationHealths).toEqual(['warning', 'critical']);
    expect(ruleFormToMatch(values)).toEqual({ healths: ['warning', 'critical'] });
  });

  it('submits an explicit state list alongside healths', () => {
    const values = ruleFormSchema.parse({
      ...base,
      name: 'repl error',
      type: 'vm_replication' as const,
      replicationHealths: ['critical'],
      replicationStates: ['error', 'discarded'],
    });
    expect(ruleFormToMatch(values)).toEqual({ healths: ['critical'], states: ['error', 'discarded'] });
  });

  it('submits a state-only rule when healths are cleared', () => {
    const values = ruleFormSchema.parse({
      ...base,
      name: 'repl state',
      type: 'vm_replication' as const,
      replicationHealths: [],
      replicationStates: ['recovery_in_progress'],
    });
    expect(ruleFormToMatch(values)).toEqual({ states: ['recovery_in_progress'] });
  });

  it('rejects an empty selection', () => {
    const result = ruleFormSchema.safeParse({
      ...base,
      name: 'empty',
      type: 'vm_replication' as const,
      replicationHealths: [],
      replicationStates: [],
    });
    expect(result.success).toBe(false);
  });

  it('round-trips through the edit form, mirroring the backend default', () => {
    // A stored rule with an empty match defaults to warning/critical at
    // evaluation; the form must present that selection on edit.
    const emptyMatch = ruleToForm({
      id: 'r1',
      name: 'repl',
      type: 'vm_replication',
      severity: 'warning',
      cooldownS: 0,
      enabled: true,
      channelIds: [],
      match: {},
    } as never);
    expect(emptyMatch.replicationHealths).toEqual(['warning', 'critical']);
    expect(emptyMatch.replicationStates).toEqual([]);

    const explicit = ruleToForm({
      id: 'r2',
      name: 'repl2',
      type: 'vm_replication',
      severity: 'critical',
      cooldownS: 0,
      enabled: true,
      channelIds: [],
      match: { healths: ['critical'], states: ['error'] },
    } as never);
    expect(explicit.replicationHealths).toEqual(['critical']);
    expect(explicit.replicationStates).toEqual(['error']);
  });
});

describe('logon rule match transformation', () => {
  it('submits outcome and users (any user when empty)', () => {
    const anyUser = ruleFormSchema.parse({ ...base, name: 'any failure', type: 'logon' as const, logonOutcome: 'failure', users: '' });
    expect(ruleFormToMatch(anyUser)).toEqual({ outcome: 'failure' });

    const specific = ruleFormSchema.parse({
      ...base,
      name: 'admin success',
      type: 'logon' as const,
      logonOutcome: 'success',
      users: 'admin,  DOMAIN\\jsmith, admin',
      sourceKinds: ['windows-agent'],
    });
    expect(ruleFormToMatch(specific)).toEqual({
      outcome: 'success',
      users: ['admin', 'DOMAIN\\jsmith'],
      sourceKinds: ['windows-agent'],
    });
  });

  it('round-trips through the edit form', () => {
    const form = ruleToForm({
      id: 'r1',
      name: 'lockouts',
      type: 'logon',
      severity: 'critical',
      cooldownS: 0,
      enabled: true,
      channelIds: [],
      match: { outcome: 'lockout', users: ['admin', 'bob'] },
    } as never);
    expect(form.type).toBe('logon');
    expect(form.logonOutcome).toBe('lockout');
    expect(form.users).toBe('admin, bob');
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
    expect(ruleSummary({ type: 'vm_replication', match: {} })).toContain('warning, critical');
    expect(ruleSummary({ type: 'vm_replication', match: { healths: ['critical'], states: ['error'] } })).toBe('Replication: critical in state error');
    expect(ruleSummary({ type: 'vm_replication', match: { states: ['discarded'] } })).toBe('Replication: any health in state discarded');
    expect(ruleSummary({ type: 'threshold', match: { metric: 'power_watts', comparator: 'gt', value: 500 } })).toContain('500');
    expect(ruleSummary({ type: 'logon', match: { outcome: 'failure', users: ['admin'] } })).toBe('Logon: Failed logon for admin');
    expect(ruleSummary({ type: 'logon', match: { outcome: 'success' } })).toBe('Logon: Successful logon for any user');
  });
});
