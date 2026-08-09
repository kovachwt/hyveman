/**
 * API barrel: the generated client, the fetch mutator, and query keys.
 * Feature code imports from '@/api' (or the generated module directly).
 */
export * from './generated/endpoints';
export * from './client';
export * from './queryKeys';

/** Response envelope helper: generated responses are `{ data, status } & { headers }`. */
export function unwrap<T>(res: { data: T }): T {
  return res.data;
}
