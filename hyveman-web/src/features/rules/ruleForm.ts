/**
 * Alert-rule forms (FRONTEND.md §8.4): type-specific selectors that submit the
 * typed match document expected by the API. Client-side schemas improve
 * feedback; the backend remains the authority. Pure helpers are unit-tested in
 * ruleForm.test.ts.
 */
import { z } from 'zod';
import type { RuleDto } from '@/api/generated/endpoints';

export const RULE_TYPES = ['health', 'event', 'heartbeat', 'threshold'] as const;
export const RULE_SEVERITIES = ['info', 'warning', 'critical'] as const;
export const SOURCE_KINDS = ['windows-agent', 'linux-agent', 'syslog-feed'] as const;
export const COMPONENT_TYPES = ['cpu', 'memory', 'disk', 'controller', 'psu', 'fan', 'temp', 'chassis', 'system', 'other'] as const;
export const HEALTH_STATES = ['ok', 'warning', 'critical', 'unknown'] as const;
export const COMPARATORS = ['gt', 'gte', 'lt', 'lte', 'eq'] as const;

export interface RuleFormValues {
  name: string;
  type: (typeof RULE_TYPES)[number];
  severity: (typeof RULE_SEVERITIES)[number];
  cooldownS: number;
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
}

export function emptyRuleForm(): RuleFormValues {
  return {
    name: '',
    type: 'event',
    severity: 'warning',
    cooldownS: 300,
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
    metric: '',
    comparator: 'gt',
    value: '',
  };
}

const intOrEmpty = z.union([z.number().int(), z.literal('')]);
const nonNegInt = z.number().int().min(0);

export const ruleFormSchema = z
  .object({
    name: z.string().trim().min(1, 'Name is required.').max(120),
    type: z.enum(RULE_TYPES),
    severity: z.enum(RULE_SEVERITIES),
    cooldownS: nonNegInt,
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
    metric: z.string().trim(),
    comparator: z.enum(COMPARATORS),
    value: intOrEmpty,
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
    case 'threshold':
      match.metric = values.metric;
      match.comparator = values.comparator;
      match.value = Number(values.value);
      break;
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
  return {
    ...base,
    name: rule.name ?? '',
    type: (rule.type as RuleFormValues['type']) ?? 'event',
    severity: ((rule.severity as RuleFormValues['severity']) ?? 'warning') as RuleFormValues['severity'],
    cooldownS: Number(rule.cooldownS) || 0,
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
    metric: typeof m.metric === 'string' ? m.metric : '',
    comparator: (typeof m.comparator === 'string' ? m.comparator : 'gt') as RuleFormValues['comparator'],
    value: num('value'),
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
    case 'threshold':
      return `Threshold: ${m.metric ?? '?'} ${m.comparator ?? 'gt'} ${m.value ?? '?'}`;
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
    enabled: values.enabled,
    channelIds: values.channelIds,
    ...(edit && updatedAt ? { updatedAt } : {}),
  };
}
