/**
 * Alert-rule forms (FRONTEND.md §8.4): type-specific selectors that submit the
 * typed match document expected by the API. Client-side schemas improve
 * feedback; the backend remains the authority. Pure helpers are unit-tested in
 * ruleForm.test.ts.
 */
import { z } from 'zod';
import type { RuleDto } from '@/api/generated/endpoints';

export const RULE_TYPES = ['health', 'event', 'heartbeat', 'threshold', 'vm_heartbeat', 'vm_replication', 'logon'] as const;
export const RULE_TYPE_LABELS: Record<(typeof RULE_TYPES)[number], string> = {
  health: 'Health state',
  event: 'Event match',
  heartbeat: 'Agent heartbeat',
  threshold: 'Metric threshold',
  vm_heartbeat: 'VM heartbeat lost',
  vm_replication: 'VM replication degraded',
  logon: 'User logon',
};
export const RULE_SEVERITIES = ['info', 'warning', 'critical'] as const;
export const SOURCE_KINDS = ['windows-agent', 'linux-agent', 'syslog-feed'] as const;
export const COMPONENT_TYPES = ['cpu', 'memory', 'disk', 'controller', 'psu', 'fan', 'temp', 'chassis', 'system', 'other'] as const;
export const HEALTH_STATES = ['ok', 'warning', 'critical', 'unknown'] as const;
export const REPLICATION_HEALTHS = ['ok', 'warning', 'critical', 'not_applicable'] as const;
export const REPLICATION_STATES = [
  'disabled',
  'error',
  'enabled',
  'replication_in_progress',
  'planned_failover_in_progress',
  'snapshot_in_progress',
  'initial_replication_in_progress',
  'initial_replication_pending',
  'recovery_in_progress',
  'failback_in_progress',
  'failback_complete',
  'discarded',
] as const;
export const COMPARATORS = ['gt', 'gte', 'lt', 'lte', 'eq'] as const;
export const LOGON_OUTCOMES = ['success', 'failure', 'lockout'] as const;
export const LOGON_OUTCOME_LABELS: Record<(typeof LOGON_OUTCOMES)[number], string> = {
  success: 'Successful logon',
  failure: 'Failed logon',
  lockout: 'Account lockout',
};

export interface RuleFormValues {
  name: string;
  type: (typeof RULE_TYPES)[number];
  severity: (typeof RULE_SEVERITIES)[number];
  cooldownS: number;
  autoResolveAfterS: number | '';
  enabled: boolean;
  channelIds: string[];
  // health
  componentTypes: string[];
  states: string[];
  includeRollup: boolean;
  // event
  channel: string;
  eventIds: string;
  severityMin: number | '';
  messagePattern: string;
  sourceKinds: string[];
  // heartbeat
  silenceAfterS: number;
  // threshold
  metric: string;
  comparator: (typeof COMPARATORS)[number];
  value: number | '';
  // vm_heartbeat / vm_replication
  replicationHealths: string[];
  replicationStates: string[];
  // logon
  logonOutcome: (typeof LOGON_OUTCOMES)[number];
  users: string;
}

export function emptyRuleForm(): RuleFormValues {
  return {
    name: '',
    type: 'event',
    severity: 'warning',
    cooldownS: 300,
    autoResolveAfterS: '',
    enabled: true,
    channelIds: [],
    componentTypes: [],
    states: ['warning', 'critical'],
    includeRollup: true,
    channel: '',
    eventIds: '',
    severityMin: '',
    messagePattern: '',
    sourceKinds: [],
    silenceAfterS: 300,
    replicationHealths: ['warning', 'critical'],
    replicationStates: [],
    metric: '',
    comparator: 'gt',
    value: '',
    logonOutcome: 'failure',
    users: '',
  };
}

const intOrEmpty = z.union([z.number().int(), z.literal('')]);
const nonNegInt = z.number().int().min(0);
const nonNegIntOrEmpty = z.union([z.number().int().min(0), z.literal('')]);

export const ruleFormSchema = z
  .object({
    name: z.string().trim().min(1, 'Name is required.').max(120),
    type: z.enum(RULE_TYPES),
    severity: z.enum(RULE_SEVERITIES),
    cooldownS: nonNegInt,
    autoResolveAfterS: nonNegIntOrEmpty,
    enabled: z.boolean(),
    channelIds: z.array(z.string()),
    componentTypes: z.array(z.string()),
    states: z.array(z.string()),
    includeRollup: z.boolean(),
    channel: z.string().trim(),
    eventIds: z.string().trim(),
    severityMin: intOrEmpty,
    messagePattern: z.string().trim(),
    sourceKinds: z.array(z.string()),
    silenceAfterS: nonNegInt,
    replicationHealths: z.array(z.string()),
    replicationStates: z.array(z.string()),
    metric: z.string().trim(),
    comparator: z.enum(COMPARATORS),
    value: intOrEmpty,
    logonOutcome: z.enum(LOGON_OUTCOMES),
    users: z.string().trim(),
  })
  .superRefine((v, ctx) => {
    if (v.type === 'health' && v.componentTypes.length === 0) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ['componentTypes'], message: 'Select at least one component type.' });
    }
    if (v.type === 'event' && !v.channel && !parseEventIds(v.eventIds) && !v.messagePattern && v.severityMin === '') {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        path: ['channel'],
        message: 'Event rules need at least one of channel, event IDs, message pattern, or minimum severity.',
      });
    }
    if (v.type === 'threshold') {
      if (!v.metric) ctx.addIssue({ code: z.ZodIssueCode.custom, path: ['metric'], message: 'Metric is required.' });
      if (v.value === '') ctx.addIssue({ code: z.ZodIssueCode.custom, path: ['value'], message: 'Threshold value is required.' });
    }
    if (v.type === 'vm_replication' && v.replicationHealths.length === 0 && v.replicationStates.length === 0) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        path: ['replicationHealths'],
        message: 'Select at least one replication health or state.',
      });
    }
  });

export type RuleFormValuesValidated = z.infer<typeof ruleFormSchema>;

export function parseEventIds(raw: string): number[] | null {
  const ids = raw
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean)
    .map(Number);
  if (ids.length === 0) return null;
  return ids.every((n) => Number.isInteger(n) && n > 0) ? ids : null;
}

/** Comma-separated account names -> unique list; null when empty. */
export function parseUsers(raw: string): string[] | null {
  const users = [...new Set(raw.split(',').map((s) => s.trim()).filter(Boolean))];
  return users.length === 0 ? null : users;
}

/** Form values -> typed match document (only the fields for the rule type). */
export function ruleFormToMatch(values: RuleFormValuesValidated): Record<string, unknown> {
  const match: Record<string, unknown> = {};
  if (values.sourceKinds.length > 0) match.sourceKinds = values.sourceKinds;
  switch (values.type) {
    case 'health':
      match.componentTypes = values.componentTypes;
      match.states = values.states;
      match.includeRollup = values.includeRollup;
      break;
    case 'event': {
      if (values.channel) match.channel = values.channel;
      const ids = parseEventIds(values.eventIds);
      if (ids) match.eventIds = ids;
      if (values.severityMin !== '') match.severityMin = values.severityMin;
      if (values.messagePattern) match.messagePattern = values.messagePattern;
      break;
    }
    case 'heartbeat':
      match.silenceAfterS = values.silenceAfterS;
      break;
    case 'vm_heartbeat':
      // No options: fires when a running VM whose heartbeat was OK goes lost.
      break;
    case 'vm_replication':
      // Backend default (both empty) is healths=["warning","critical"]; the
      // form pre-selects that default, so healths is sent explicitly.
      if (values.replicationHealths.length > 0) match.healths = values.replicationHealths;
      if (values.replicationStates.length > 0) match.states = values.replicationStates;
      break;
    case 'threshold':
      match.metric = values.metric;
      match.comparator = values.comparator;
      match.value = Number(values.value);
      break;
    case 'logon': {
      match.outcome = values.logonOutcome;
      const users = parseUsers(values.users);
      if (users) match.users = users;
      break;
    }
  }
  return match;
}

/** RuleDto -> form values (edit mode); unknown match fields are ignored. */
export function ruleToForm(rule: RuleDto): RuleFormValues {
  const base = emptyRuleForm();
  const m = rule.match ?? {};
  const strings = (key: string): string[] => {
    const v = m[key];
    return Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : [];
  };
  const num = (key: string): number | '' => {
    const v = m[key];
    const n = typeof v === 'number' ? v : typeof v === 'string' ? Number(v) : NaN;
    return Number.isFinite(n) ? n : '';
  };
  // Backend default for vm_replication (both keys absent ⇒ healths =
  // warning/critical at evaluation time); the form mirrors it so an
  // untouched edit round-trips without inventing match fields.
  const replicationHealths = strings('healths');
  const replicationStates = strings('states');
  const effectiveHealths =
    replicationHealths.length > 0 || replicationStates.length > 0
      ? replicationHealths
      : ['warning', 'critical'];
  return {
    ...base,
    name: rule.name ?? '',
    type: (rule.type as RuleFormValues['type']) ?? 'event',
    severity: ((rule.severity as RuleFormValues['severity']) ?? 'warning') as RuleFormValues['severity'],
    cooldownS: Number(rule.cooldownS) || 0,
    autoResolveAfterS: rule.autoResolveAfterS != null ? Number(rule.autoResolveAfterS) : '',
    enabled: rule.enabled ?? true,
    channelIds: rule.channelIds ?? [],
    componentTypes: strings('componentTypes'),
    states: strings('states'),
    includeRollup: typeof m.includeRollup === 'boolean' ? m.includeRollup : true,
    channel: typeof m.channel === 'string' ? m.channel : '',
    eventIds: Array.isArray(m.eventIds) ? m.eventIds.join(', ') : '',
    severityMin: m.severityMin != null ? num('severityMin') : '',
    messagePattern: typeof m.messagePattern === 'string' ? m.messagePattern : '',
    sourceKinds: strings('sourceKinds'),
    silenceAfterS: num('silenceAfterS') === '' ? 300 : Number(num('silenceAfterS')),
    replicationHealths: effectiveHealths,
    replicationStates,
    metric: typeof m.metric === 'string' ? m.metric : '',
    comparator: (typeof m.comparator === 'string' ? m.comparator : 'gt') as RuleFormValues['comparator'],
    value: num('value'),
    logonOutcome: (LOGON_OUTCOMES as readonly string[]).includes(String(m.outcome))
      ? (m.outcome as RuleFormValues['logonOutcome'])
      : 'failure',
    users: Array.isArray(m.users) ? (m.users as unknown[]).filter((x): x is string => typeof x === 'string').join(', ') : '',
  };
}

/** Human-readable one-line summary of a rule's match document. */
export function ruleSummary(rule: Pick<RuleDto, 'type' | 'match'>): string {
  const m = rule.match ?? {};
  switch (rule.type) {
    case 'health': {
      const components = Array.isArray(m.componentTypes) ? (m.componentTypes as string[]).join(', ') : 'any component';
      const states = Array.isArray(m.states) && (m.states as string[]).length > 0 ? (m.states as string[]).join(', ') : 'warning/critical';
      return `Health: ${components} in state ${states}${m.includeRollup === false ? ' (excludes rollup)' : ''}`;
    }
    case 'event': {
      const parts: string[] = [];
      if (m.channel) parts.push(`channel=${m.channel}`);
      if (Array.isArray(m.eventIds) && (m.eventIds as unknown[]).length > 0) parts.push(`eventIds=${(m.eventIds as unknown[]).join(',')}`);
      if (m.severityMin != null) parts.push(`severity≥${m.severityMin}`);
      if (m.messagePattern) parts.push(`message≈${m.messagePattern}`);
      return `Event: ${parts.join(' · ') || 'any event'}`;
    }
    case 'heartbeat':
      return `Heartbeat: silent for ${m.silenceAfterS ?? 300}s`;
    case 'vm_heartbeat':
      return 'VM heartbeat: fires when a running VM with a prior OK heartbeat goes lost';
    case 'vm_replication': {
      // Mirrors the backend default: a match with neither key alerts on
      // warning/critical replication health.
      const healths = Array.isArray(m.healths) && (m.healths as unknown[]).length > 0
        ? (m.healths as string[]).join(', ')
        : Array.isArray(m.states) && (m.states as unknown[]).length > 0 ? null : 'warning, critical';
      const states = Array.isArray(m.states) && (m.states as unknown[]).length > 0
        ? (m.states as string[]).join(', ')
        : null;
      return `Replication: ${healths ?? 'any health'}${states ? ` in state ${states}` : ''}`;
    }
    case 'threshold':
      return `Threshold: ${m.metric ?? '?'} ${m.comparator ?? 'gt'} ${m.value ?? '?'}`;
    case 'logon': {
      const outcome = (LOGON_OUTCOME_LABELS as Record<string, string>)[String(m.outcome)] ?? String(m.outcome ?? '?');
      const users = Array.isArray(m.users) && (m.users as unknown[]).length > 0
        ? (m.users as unknown[]).join(', ')
        : 'any user';
      return `Logon: ${outcome} for ${users}`;
    }
    default:
      return rule.type ?? 'unknown';
  }
}

/** Builds the API input from form values. */
export function buildRuleInput(values: RuleFormValuesValidated, edit: boolean, updatedAt?: string) {
  return {
    name: values.name,
    type: values.type,
    match: ruleFormToMatch(values),
    severity: values.severity,
    cooldownS: values.cooldownS,
    // Blank = never: an explicit 0 round-trips through the API's optional
    // field (null on patch means "leave unchanged").
    autoResolveAfterS: values.autoResolveAfterS === '' ? 0 : values.autoResolveAfterS,
    enabled: values.enabled,
    channelIds: values.channelIds,
    ...(edit && updatedAt ? { updatedAt } : {}),
  };
}
