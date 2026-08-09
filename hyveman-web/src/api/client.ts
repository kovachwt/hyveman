/**
 * Handwritten API client glue (FRONTEND.md §6.2). The Orval-generated client
 * (src/api/generated) delegates every request to `httpFetch`, which owns:
 *
 *  - the relative `/api/v1` base path (same-origin deployment is preferred);
 *  - `credentials: "include"` for the HttpOnly session cookie;
 *  - the API-issued CSRF header/cookie pair for unsafe methods (the API, not
 *    the frontend, invents the token — the `hyveman_csrf` cookie is issued by
 *    the API on any `/api/v1` response, GETs included);
 *  - RFC 9457 Problem Details parsing into a typed `ApiError`;
 *  - cancellation signal passthrough for TanStack Query; and
 *  - a global 401 hook so the auth context can react to session expiry.
 *
 * The mutator returns the Orval response envelope `{ data, status, headers }`
 * (the generated types declare it), where `data` is the parsed JSON body of
 * the raw DTO the API serializes — the API does not wrap responses in an
 * outer `data` object (API.md §5.2, ApiContractTests.cs). The mock API and
 * the Vitest fetch stub must therefore serve raw DTO bodies too.
 *
 * This module never logs request bodies: they may contain secret fields.
 */

export const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '/api/v1';

export const CSRF_COOKIE = 'hyveman_csrf';
export const CSRF_HEADER = 'X-CSRF-Token';

/** RFC 9457 Problem Details as produced by hyveman-api (API.md §5.2). */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  /** Stable machine-readable code; the UI branches on this, never on detail text. */
  code?: string;
  detail?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
}

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly detail?: string;
  readonly traceId?: string;
  readonly errors?: Record<string, string[]>;
  readonly problem?: ProblemDetails;

  constructor(problem: ProblemDetails | undefined, status: number, message?: string, cause?: unknown) {
    super(message ?? problem?.title ?? `Request failed (HTTP ${status})`, { cause });
    this.name = 'ApiError';
    this.status = status;
    this.code = problem?.code ?? (status === 0 ? 'network_error' : 'http_error');
    this.detail = problem?.detail;
    this.traceId = problem?.traceId;
    this.errors = problem?.errors;
    this.problem = problem;
  }

  get isNetworkError(): boolean {
    return this.status === 0;
  }

  get isUnauthorized(): boolean {
    return this.status === 401;
  }
}

/** Called when any request receives 401 (AuthProvider invalidates the session). */
let unauthorizedHandler: (() => void) | null = null;
export function setUnauthorizedHandler(handler: (() => void) | null): void {
  unauthorizedHandler = handler;
}

export function readCookie(name: string): string | undefined {
  const prefix = `${name}=`;
  for (const part of document.cookie.split(';')) {
    const trimmed = part.trim();
    if (trimmed.startsWith(prefix)) return decodeURIComponent(trimmed.slice(prefix.length));
  }
  return undefined;
}

const UNSAFE_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

/** Ensures the CSRF cookie exists before an unsafe request (the API issues it
 *  on any /api/v1 response). A GET to auth/session is the cheapest trigger. */
async function ensureCsrfCookie(): Promise<void> {
  if (readCookie(CSRF_COOKIE)) return;
  try {
    await fetch(`${API_BASE}/auth/session`, {
      credentials: 'include',
      headers: { Accept: 'application/json' },
      cache: 'no-store',
    });
  } catch {
    // Network failure is reported by the real request; nothing to do here.
  }
}

async function parseProblem(res: Response): Promise<ProblemDetails | undefined> {
  const text = await res.text().catch(() => '');
  if (!text) return undefined;
  try {
    return JSON.parse(text) as ProblemDetails;
  } catch {
    return undefined;
  }
}

/**
 * Fetch mutator consumed by the generated client:
 *   httpFetch<T>(url, { method, headers, body, signal }) => Promise<T>
 */
export async function httpFetch<T>(url: string, options?: RequestInit): Promise<T> {
  const method = (options?.method ?? 'GET').toUpperCase();
  const headers = new Headers(options?.headers);
  headers.set('Accept', 'application/json');
  if (options?.body != null && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json');
  }

  if (UNSAFE_METHODS.has(method)) {
    await ensureCsrfCookie();
    const csrf = readCookie(CSRF_COOKIE);
    if (csrf) headers.set(CSRF_HEADER, csrf);
  }

  let res: Response;
  try {
    res = await fetch(url, {
      ...options,
      headers,
      credentials: 'include',
      cache: 'no-store',
    });
  } catch (err) {
    throw new ApiError(
      { code: 'network_error', title: 'Network error' },
      0,
      'Could not reach the Hyveman API. Check the connection and retry.',
      err,
    );
  }

  if (!res.ok) {
    const problem = await parseProblem(res);
    if (res.status === 401) unauthorizedHandler?.();
    throw new ApiError(problem, res.status);
  }

  if (res.status === 204) {
    return { data: undefined, status: 204, headers: res.headers } as T;
  }
  const body = (await res.json()) as unknown;
  return { data: body, status: res.status, headers: res.headers } as T;
}
