/**
 * Coercion helpers for the generated DTOs (Orval marks every property optional
 * and maps int64 to `number | string`). API timestamps/ids are opaque strings;
 * these helpers keep feature code free of undefined-chains without loosening
 * types at the API boundary.
 */

export function num(value: unknown): number | null {
  if (value == null || value === '') return null;
  const n = typeof value === 'string' ? Number(value) : (value as number);
  return Number.isFinite(n) ? n : null;
}

export function numOr(value: unknown, fallback: number): number {
  return num(value) ?? fallback;
}

export function str(value: unknown): string | undefined {
  if (value == null) return undefined;
  return String(value);
}

export function bool(value: unknown, fallback = false): boolean {
  return typeof value === 'boolean' ? value : fallback;
}
