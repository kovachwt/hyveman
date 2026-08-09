import { describe, expect, it } from 'vitest';
import {
  buildHostInput,
  emptyHostForm,
  hostFormFromDto,
  hostFormSchema,
  isAllowedIdracUrl,
} from './hostForm';

describe('isAllowedIdracUrl', () => {
  it('accepts https URLs without user info or fragments', () => {
    expect(isAllowedIdracUrl('https://idrac.example.internal')).toBe(true);
    expect(isAllowedIdracUrl('https://idrac.example:443/redfish')).toBe(true);
  });

  it('rejects insecure and malformed URLs', () => {
    expect(isAllowedIdracUrl('http://idrac.example')).toBe(false);
    expect(isAllowedIdracUrl('https://user:pass@idrac.example')).toBe(false);
    expect(isAllowedIdracUrl('https://idrac.example/#frag')).toBe(false);
    expect(isAllowedIdracUrl('not a url')).toBe(false);
  });
});

describe('host form validation', () => {
  it('requires a name', () => {
    const result = hostFormSchema.safeParse(emptyHostForm());
    expect(result.success).toBe(false);
    if (!result.success) {
      expect(result.error.issues.some((i) => i.path[0] === 'name')).toBe(true);
    }
  });

  it('requires both iDRAC credentials when either is set', () => {
    const base = { ...emptyHostForm(), name: 'host1' };
    const userOnly = hostFormSchema.safeParse({ ...base, idracUsername: 'root' });
    expect(userOnly.success).toBe(false);
    const passOnly = hostFormSchema.safeParse({ ...base, idracPassword: 'pw' });
    expect(passOnly.success).toBe(false);
    const both = hostFormSchema.safeParse({ ...base, idracUsername: 'root', idracPassword: 'pw' });
    expect(both.success).toBe(true);
  });
});

describe('buildHostInput (write-only credentials)', () => {
  const values = hostFormSchema.parse({
    ...emptyHostForm(),
    name: '  host1  ',
    kind: 'windows-server',
    idracUrl: 'https://idrac.example',
    idracUsername: 'root',
    idracPassword: 'pw',
    enabled: true,
    notes: '',
    sourceId: 'src_1',
  });

  it('sends credentials on create', () => {
    const input = buildHostInput(values, false);
    expect(input.name).toBe('host1');
    expect(input.idracUsername).toBe('root');
    expect(input.idracPassword).toBe('pw');
    expect(input.sourceId).toBe('src_1');
  });

  it('omits blank credentials on edit (leave unchanged), no updatedAt on create', () => {
    const editValues = { ...values, idracUsername: '', idracPassword: '' };
    const input = buildHostInput(editValues, true, '2025-08-09T00:00:00Z');
    expect(input.idracUsername).toBeUndefined();
    expect(input.idracPassword).toBeUndefined();
    expect(input.updatedAt).toBe('2025-08-09T00:00:00Z');
  });

  it('sends newly entered credentials on edit', () => {
    const input = buildHostInput(values, true, undefined);
    expect(input.idracUsername).toBe('root');
    expect(input.idracPassword).toBe('pw');
  });
});

describe('hostFormFromDto', () => {
  it('never fills secret fields from the DTO (API never echoes them)', () => {
    const form = hostFormFromDto({
      id: 'h1',
      name: 'host1',
      kind: 'windows-server',
      idracCredentialSet: true,
      enabled: true,
      createdAt: '2025-01-01T00:00:00Z',
      updatedAt: '2025-01-01T00:00:00Z',
    } as never);
    expect(form.idracUsername).toBe('');
    expect(form.idracPassword).toBe('');
  });
});
