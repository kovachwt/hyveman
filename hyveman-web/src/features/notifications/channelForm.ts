/**
 * Notification channel forms (FRONTEND.md §8.5): secrets are write-only —
 * blank on edit means "leave current value unchanged", responses never echo
 * them, and entered values are cleared after submission. Pure helpers are
 * unit-tested in channelForm.test.ts.
 */
import { z } from 'zod';
import type { ChannelDto, ChannelInput } from '@/api/generated/endpoints';

export const CHANNEL_KINDS = ['telegram', 'webhook', 'smtp'] as const;

export interface ChannelFormValues {
  name: string;
  kind: (typeof CHANNEL_KINDS)[number];
  enabled: boolean;
  telegramBotToken: string;
  telegramChatId: string;
  webhookUrl: string;
  smtpHost: string;
  smtpPort: string;
  smtpUsername: string;
  smtpPassword: string;
  smtpFrom: string;
  smtpTo: string;
  smtpUseTls: boolean;
}

export function emptyChannelForm(): ChannelFormValues {
  return {
    name: '',
    kind: 'telegram',
    enabled: true,
    telegramBotToken: '',
    telegramChatId: '',
    webhookUrl: '',
    smtpHost: '',
    smtpPort: '587',
    smtpUsername: '',
    smtpPassword: '',
    smtpFrom: '',
    smtpTo: '',
    smtpUseTls: true,
  };
}

export function channelFormSchema(edit = false) {
  return z
    .object({
      name: z.string().trim().min(1, 'Name is required.').max(120),
      kind: z.enum(CHANNEL_KINDS),
      enabled: z.boolean(),
      telegramBotToken: z.string().trim(),
      telegramChatId: z.string().trim(),
      webhookUrl: z.string().trim().refine((v) => v === '' || isHttpsUrl(v), 'Webhook URL must be https://.'),
      smtpHost: z.string().trim(),
      smtpPort: z
        .string()
        .refine((v) => v === '' || (Number.isInteger(Number(v)) && Number(v) >= 1 && Number(v) <= 65535), 'Port must be 1–65535.'),
      smtpUsername: z.string().trim(),
      smtpPassword: z.string(),
      smtpFrom: z.string().trim().refine((v) => v === '' || v.includes('@'), 'Must be an email address.'),
      smtpTo: z.string().trim().refine((v) => v === '' || v.includes('@'), 'Must be an email address.'),
      smtpUseTls: z.boolean(),
    })
    .superRefine((v, ctx) => {
      const anySecret = (fields: string[]) => fields.some((f) => v[f as keyof typeof v] !== '');
      if (v.kind === 'telegram') {
        const required = !v.telegramBotToken || !v.telegramChatId;
        if ((!edit || anySecret(['telegramBotToken', 'telegramChatId'])) && required) {
          ctx.addIssue({ code: z.ZodIssueCode.custom, path: ['telegramBotToken'], message: 'Both the bot token and chat ID are required for Telegram.' });
        }
      }
      if (v.kind === 'webhook' && (!edit || anySecret(['webhookUrl'])) && !v.webhookUrl) {
        ctx.addIssue({ code: z.ZodIssueCode.custom, path: ['webhookUrl'], message: 'Webhook URL is required.' });
      }
      if (v.kind === 'smtp') {
        const anySmtp = anySecret(['smtpHost', 'smtpUsername', 'smtpPassword', 'smtpFrom', 'smtpTo']);
        if ((!edit || anySmtp) && (!v.smtpHost || !v.smtpTo)) {
          ctx.addIssue({ code: z.ZodIssueCode.custom, path: ['smtpHost'], message: 'SMTP host and recipient are required.' });
        }
      }
    });
}

export type ChannelFormValuesValidated = z.infer<ReturnType<typeof channelFormSchema>>;

export function isHttpsUrl(value: string): boolean {
  try {
    return new URL(value).protocol === 'https:';
  } catch {
    return false;
  }
}

/** Builds the API request. On edit, blank config fields are omitted so the
 *  stored values stay unchanged (write-only semantics). */
export function buildChannelInput(values: ChannelFormValuesValidated, edit: boolean, updatedAt?: string): ChannelInput {
  const config: NonNullable<ChannelInput['config']> = {};

  const put = <K extends keyof typeof config>(key: K, value: string | boolean | number | undefined) => {
    if (edit) {
      if (typeof value === 'string' ? value.trim() !== '' : value !== undefined) (config as Record<K, unknown>)[key] = value as never;
    } else if (value !== undefined) {
      (config as Record<K, unknown>)[key] = value as never;
    }
  };

  if (values.kind === 'telegram') {
    put('telegramBotToken', values.telegramBotToken.trim() || undefined);
    put('telegramChatId', values.telegramChatId.trim() || undefined);
  } else if (values.kind === 'webhook') {
    put('webhookUrl', values.webhookUrl.trim() || undefined);
  } else {
    put('smtpHost', values.smtpHost.trim() || undefined);
    put('smtpPort', values.smtpPort ? Number(values.smtpPort) : undefined);
    put('smtpUsername', values.smtpUsername.trim() || undefined);
    put('smtpPassword', values.smtpPassword || undefined);
    put('smtpFrom', values.smtpFrom.trim() || undefined);
    put('smtpTo', values.smtpTo.trim() || undefined);
    put('smtpUseTls', values.smtpUseTls);
  }

  return {
    name: values.name,
    kind: values.kind,
    enabled: values.enabled,
    config: Object.keys(config).length > 0 ? config : undefined,
    ...(edit && updatedAt ? { updatedAt } : {}),
  };
}

/** Edit forms start from the redacted summary: names/kind/enabled only, no
 *  secret values (they are never echoed by the API). */
export function channelFormFromDto(channel: ChannelDto): ChannelFormValues {
  const base = emptyChannelForm();
  return {
    ...base,
    name: channel.name ?? '',
    kind: (channel.kind as ChannelFormValues['kind']) ?? 'telegram',
    enabled: channel.enabled ?? true,
    smtpPort: '587',
  };
}

export const CHANNEL_KIND_LABELS: Record<(typeof CHANNEL_KINDS)[number], string> = {
  telegram: 'Telegram',
  webhook: 'Webhook',
  smtp: 'SMTP',
};
