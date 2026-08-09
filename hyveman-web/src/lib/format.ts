/** Number/unit formatting helpers (unit-tested in lib/format.test.ts). */

/** 1234 -> "1,234"; 12.5 -> "12.5". Never emits NaN/"undefined". */
export function formatCount(value: number | string | null | undefined, digits = 0): string {
  const n = typeof value === 'string' ? Number(value) : value;
  if (n == null || !Number.isFinite(n)) return '—';
  return n.toLocaleString(undefined, { maximumFractionDigits: digits });
}

export function formatPercent(value: number | string | null | undefined): string {
  const n = typeof value === 'string' ? Number(value) : value;
  if (n == null || !Number.isFinite(n)) return '—';
  return `${Math.round(n)}%`;
}

/** 512 -> "512 B", 4096 -> "4.0 KiB", 7_340_032 -> "7.0 MiB". */
export function formatBytes(bytes: number | string | null | undefined): string {
  const n = typeof bytes === 'string' ? Number(bytes) : bytes;
  if (n == null || !Number.isFinite(n)) return '—';
  if (n < 1024) return `${n} B`;
  const units = ['KiB', 'MiB', 'GiB', 'TiB', 'PiB'];
  let value = n;
  let unit = -1;
  do {
    value /= 1024;
    unit += 1;
  } while (value >= 1024 && unit < units.length - 1);
  return `${value.toFixed(value >= 100 ? 0 : 1)} ${units[unit]}`;
}

/** 90 -> "1m 30s"; 7200 -> "2h"; 90061 -> "1d 1h". */
export function formatDuration(totalSeconds: number | string | null | undefined): string {
  const s = typeof totalSeconds === 'string' ? Number(totalSeconds) : totalSeconds;
  if (s == null || !Number.isFinite(s) || s < 0) return '—';
  const secs = Math.round(s);
  const d = Math.floor(secs / 86400);
  const h = Math.floor((secs % 86400) / 3600);
  const m = Math.floor((secs % 3600) / 60);
  const parts: string[] = [];
  if (d > 0) parts.push(`${d}d`);
  if (h > 0) parts.push(`${h}h`);
  if (m > 0) parts.push(`${m}m`);
  if (secs % 60 > 0) parts.push(`${secs % 60}s`);
  if (parts.length === 0) parts.push(`${secs}s`);
  // Show at most the two most significant units (90 -> "1m 30s", 90061 -> "1d 1h").
  return parts.slice(0, 2).join(' ');
}

/** "2025-08-09T14:32:05Z" -> "Aug 9, 2025 2:32 PM". */
export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  return d.toLocaleString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });
}

/** UTC representation for tooltips ("Aug 9, 2025 14:32:05 UTC"). */
export function formatUtcDateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  return `${d.toLocaleString('en-US', {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    timeZone: 'UTC',
  })} UTC`;
}

/** Relative age such as "2 minutes ago"; older than 30 days falls back to the
 *  absolute local date. Callers always pair this with an absolute timestamp. */
export function relativeTime(iso: string | null | undefined, now: number = Date.now()): string {
  if (!iso) return '—';
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return '—';
  const diffMs = Math.max(0, now - then);
  const diffS = Math.floor(diffMs / 1000);
  if (diffS < 5) return 'just now';
  if (diffS < 60) return `${diffS} seconds ago`;
  const diffM = Math.floor(diffS / 60);
  if (diffM < 60) return diffM === 1 ? '1 minute ago' : `${diffM} minutes ago`;
  const diffH = Math.floor(diffM / 60);
  if (diffH < 24) return diffH === 1 ? '1 hour ago' : `${diffH} hours ago`;
  const diffD = Math.floor(diffH / 24);
  if (diffD < 30) return diffD === 1 ? '1 day ago' : `${diffD} days ago`;
  return new Date(then).toLocaleDateString();
}

/** UTC calendar day label for logon stats ("2025-08-09 (UTC)"). */
export function utcDayLabel(day: string): string {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(day)) return day;
  return `${day} (UTC)`;
}

/** ISO instant -> local "YYYY-MM-DDTHH:mm" value for datetime-local inputs. */
export function toLocalDateTimeInput(iso: string | undefined): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
