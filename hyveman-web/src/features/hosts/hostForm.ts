/**
 * Host create/edit form (FRONTEND.md §8.6): iDRAC credentials are write-only —
 * both required when setting them, blank on edit means "leave the stored value
 * unchanged", and the built request carries only a credential-set flag back.
 * Pure helpers are unit-tested in hostForm.test.ts.
 */
import { z } from 'zod';
import type { HostDto, HostInput } from '@/api/generated/endpoints';

export const HOST_KINDS = ['windows-server', 'linux-server', 'other'] as const;

export interface HostFormValues {
  name: string;
  kind: string;
  sourceId: string;
  idracUrl: string;
  idracUsername: string;
  idracPassword: string;
  enabled: boolean;
  notes: string;
}

export function emptyHostForm(): HostFormValues {
  return { name: '', kind: 'windows-server', sourceId: '', idracUrl: '', idracUsername: '', idracPassword: '', enabled: true, notes: '' };
}

export function hostFormFromDto(host: HostDto): HostFormValues {
  return {
    name: host.name ?? '',
    kind: host.kind ?? '',
    sourceId: host.sourceId ?? '',
    idracUrl: host.idracUrl ?? '',
    // Secrets are never echoed by the API; edit forms start blank.
    idracUsername: '',
    idracPassword: '',
    enabled: host.enabled ?? true,
    notes: host.notes ?? '',
  };
}

export const hostFormSchema = z
  .object({
    name: z.string().trim().min(1, 'Name is required.').max(120, 'Name is too long.'),
    kind: z.string().trim().min(1, 'Kind is required.'),
    sourceId: z.string(),
    idracUrl: z
      .string()
      .trim()
      .refine((v) => v === '' || isAllowedIdracUrl(v), 'iDRAC URL must be an https:// URL without user info.'),
    idracUsername: z.string().trim(),
    idracPassword: z.string(),
    enabled: z.boolean(),
    notes: z.string().max(2000, 'Notes are too long.'),
  })
  .superRefine((v, ctx) => {
    const creds = v.idracUsername.length > 0 || v.idracPassword.length > 0;
    if (creds && (v.idracUsername.length === 0 || v.idracPassword.length === 0)) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        path: ['idracUsername'],
        message: 'Both iDRAC username and password are required when setting credentials.',
      });
    }
  });

export type HostFormValuesValidated = z.infer<typeof hostFormSchema>;

/** Same validation the API applies (API.md §7.1). */
export function isAllowedIdracUrl(url: string): boolean {
  try {
    const uri = new URL(url);
    return uri.protocol === 'https:' && uri.username === '' && uri.hash === '';
  } catch {
    return false;
  }
}

/** Builds the API request. On edit, blank secrets are omitted so the stored
 *  value is left unchanged; entered secrets are sent only in this request. */
export function buildHostInput(values: HostFormValuesValidated, edit: boolean, updatedAt?: string): HostInput {
  const input: HostInput = {
    name: values.name,
    kind: values.kind,
    sourceId: values.sourceId || null,
    idracUrl: values.idracUrl || null,
    enabled: values.enabled,
    notes: values.notes || null,
  };
  if (!edit || values.idracUsername || values.idracPassword) {
    input.idracUsername = values.idracUsername || null;
    input.idracPassword = values.idracPassword || null;
  }
  if (edit && updatedAt) input.updatedAt = updatedAt;
  return input;
}

export const hostKindsLabel = (kind: string): string => kind;
