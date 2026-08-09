import { describe, expect, it } from 'vitest';
import {
  buildChannelInput,
  channelFormFromDto,
  channelFormSchema,
  emptyChannelForm,
  isHttpsUrl,
} from './channelForm';

describe('isHttpsUrl', () => {
  it('requires https', () => {
    expect(isHttpsUrl('https://hooks.example.com/x')).toBe(true);
    expect(isHttpsUrl('http://hooks.example.com/x')).toBe(false);
    expect(isHttpsUrl('file:///etc/passwd')).toBe(false);
  });
});

describe('channel form validation', () => {
  it('requires kind-specific secrets on create', () => {
    expect(channelFormSchema(false).safeParse({ ...emptyChannelForm(), name: 'tg', kind: 'telegram' }).success).toBe(false);
    expect(channelFormSchema(false).safeParse({ ...emptyChannelForm(), name: 'wh', kind: 'webhook' }).success).toBe(false);
    expect(channelFormSchema(false).safeParse({ ...emptyChannelForm(), name: 'smtp', kind: 'smtp' }).success).toBe(false);
  });

  it('allows blank secrets on edit (leave unchanged), but not partial sets', () => {
    const base = { ...emptyChannelForm(), name: 'tg' };
    // Blank on edit is fine.
    expect(channelFormSchema(true).safeParse({ ...base, kind: 'telegram' }).success).toBe(true);
    expect(channelFormSchema(true).safeParse({ ...base, kind: 'webhook' }).success).toBe(true);
    expect(channelFormSchema(true).safeParse({ ...base, kind: 'smtp' }).success).toBe(true);
    // Entering only one of a required pair is still rejected on edit.
    expect(channelFormSchema(true).safeParse({ ...base, kind: 'telegram', telegramBotToken: '123:abc' }).success).toBe(false);
    expect(channelFormSchema(true).safeParse({ ...base, kind: 'smtp', smtpHost: 'smtp.example.com' }).success).toBe(false);
  });

  it('accepts complete channel configs', () => {
    const tg = channelFormSchema(false).safeParse({
      ...emptyChannelForm(),
      name: 'tg',
      kind: 'telegram',
      telegramBotToken: '123:abc',
      telegramChatId: '-100123',
    });
    expect(tg.success).toBe(true);

    const wh = channelFormSchema(false).safeParse({
      ...emptyChannelForm(),
      name: 'wh',
      kind: 'webhook',
      webhookUrl: 'https://hooks.example.com/x',
    });
    expect(wh.success).toBe(true);

    const smtp = channelFormSchema(false).safeParse({
      ...emptyChannelForm(),
      name: 'smtp',
      kind: 'smtp',
      smtpHost: 'smtp.example.com',
      smtpTo: 'ops@example.com',
    });
    expect(smtp.success).toBe(true);
  });
});

describe('buildChannelInput (write-only secrets)', () => {
  const values = channelFormSchema(false).parse({
    ...emptyChannelForm(),
    name: 'tg',
    kind: 'telegram',
    telegramBotToken: '123:secret',
    telegramChatId: '-100123',
  });

  it('sends secrets on create', () => {
    const input = buildChannelInput(values, false);
    expect(input.config?.telegramBotToken).toBe('123:secret');
    expect(input.config?.telegramChatId).toBe('-100123');
  });

  it('omits blank secrets on edit so stored values stay unchanged', () => {
    const blank = { ...values, telegramBotToken: '', telegramChatId: '' };
    const input = buildChannelInput(blank, true, '2025-08-09T00:00:00Z');
    expect(input.config).toBeUndefined();
    expect(input.name).toBe('tg');
    expect(input.updatedAt).toBe('2025-08-09T00:00:00Z');
  });

  it('sends newly entered secrets on edit', () => {
    const input = buildChannelInput(values, true);
    expect(input.config?.telegramBotToken).toBe('123:secret');
  });

  it('keeps non-secret SMTP config but only when provided on edit', () => {
    const smtp = channelFormSchema(false).parse({
      ...emptyChannelForm(),
      name: 'mail',
      kind: 'smtp',
      smtpHost: 'smtp.example.com',
      smtpPort: '465',
      smtpTo: 'ops@example.com',
      smtpUseTls: true,
    });
    const input = buildChannelInput(smtp, true);
    expect(input.config?.smtpHost).toBe('smtp.example.com');
    expect(input.config?.smtpPort).toBe(465);
    expect(input.config?.smtpUseTls).toBe(true);
    expect(input.config?.smtpPassword).toBeUndefined();
  });
});

describe('channelFormFromDto', () => {
  it('hydrates only non-secret metadata (redacted summary)', () => {
    const form = channelFormFromDto({
      id: 'c1',
      name: 'tg',
      kind: 'telegram',
      enabled: false,
      created: '2025-01-01T00:00:00Z',
      configSummary: { botToken: 'redacted' },
    } as never);
    expect(form.name).toBe('tg');
    expect(form.kind).toBe('telegram');
    expect(form.enabled).toBe(false);
    expect(form.telegramBotToken).toBe('');
    expect(form.telegramChatId).toBe('');
  });
});
