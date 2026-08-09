import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, CSRF_HEADER, httpFetch, setUnauthorizedHandler } from './client';

describe('httpFetch', () => {
  beforeEach(() => {
    document.cookie = 'hyveman_csrf=token-123; path=/';
    vi.stubGlobal('fetch', vi.fn());
  });
  afterEach(() => {
    setUnauthorizedHandler(null);
  });

  it('sends credentials and JSON accept headers on every request', async () => {
    vi.mocked(fetch).mockResolvedValue(
      new Response(JSON.stringify({ ok: true }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
    const result = await httpFetch<{ data: { ok: boolean }; status: number; headers: Headers }>('/api/v1/test', { method: 'GET' });
    const [url, init] = vi.mocked(fetch).mock.calls[0]!;
    expect(url).toBe('/api/v1/test');
    expect(new Headers(init?.headers).get('Accept')).toBe('application/json');
    expect(init?.credentials).toBe('include');
    // Orval mutator contract: the envelope carries the raw DTO body (the API
    // does not wrap responses in an outer `data` object).
    expect(result.data).toEqual({ ok: true });
    expect(result.status).toBe(200);
    expect(result.headers.get('Content-Type')).toBe('application/json');
  });

  it('adds the API-issued CSRF header pair for unsafe methods only', async () => {
    vi.mocked(fetch).mockResolvedValue(
      new Response(JSON.stringify({}), { status: 200, headers: { 'Content-Type': 'application/json' } }),
    );
    await httpFetch('/api/v1/hosts', { method: 'POST', body: JSON.stringify({ name: 'x' }) });
    const [, init] = vi.mocked(fetch).mock.calls[0]!;
    expect(new Headers(init?.headers).get(CSRF_HEADER)).toBe('token-123');
    expect(new Headers(init?.headers).get('Content-Type')).toBe('application/json');

    vi.mocked(fetch).mockClear();
    vi.mocked(fetch).mockResolvedValue(
      new Response(JSON.stringify({}), { status: 200, headers: { 'Content-Type': 'application/json' } }),
    );
    await httpFetch('/api/v1/overview', { method: 'GET' });
    const [, getInit] = vi.mocked(fetch).mock.calls[0]!;
    expect(new Headers(getInit?.headers).get(CSRF_HEADER)).toBeNull();
  });

  it('parses RFC 9457 Problem Details into a typed ApiError with stable code', async () => {
    vi.mocked(fetch).mockResolvedValue(
      new Response(
        JSON.stringify({
          type: 'https://hyveman.example/errors/validation',
          title: 'Validation failed',
          status: 400,
          code: 'validation_failed',
          detail: 'One or more fields are invalid.',
          traceId: 'trace-1',
          errors: { name: ['Name is required.'] },
        }),
        { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    );
    const err = await httpFetch('/api/v1/hosts', { method: 'POST' }).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    const apiError = err as ApiError;
    expect(apiError.status).toBe(400);
    expect(apiError.code).toBe('validation_failed');
    expect(apiError.detail).toBe('One or more fields are invalid.');
    expect(apiError.traceId).toBe('trace-1');
    expect(apiError.errors?.['name']).toEqual(['Name is required.']);
  });

  it('maps network failures to status 0 network_error', async () => {
    vi.mocked(fetch).mockRejectedValue(new TypeError('Failed to fetch'));
    const err = (await httpFetch('/api/v1/overview').catch((e: unknown) => e)) as ApiError;
    expect(err.isNetworkError).toBe(true);
    expect(err.code).toBe('network_error');
  });

  it('notifies the global 401 handler on unauthorized responses', async () => {
    const onUnauthorized = vi.fn();
    setUnauthorizedHandler(onUnauthorized);
    vi.mocked(fetch).mockResolvedValue(
      new Response(JSON.stringify({ title: 'Unauthorized', status: 401, code: 'unauthorized' }), {
        status: 401,
        headers: { 'Content-Type': 'application/problem+json' },
      }),
    );
    await httpFetch('/api/v1/overview').catch(() => undefined);
    expect(onUnauthorized).toHaveBeenCalledTimes(1);
  });

  it('returns the envelope with undefined data for 204 No Content', async () => {
    vi.mocked(fetch).mockResolvedValue(new Response(null, { status: 204 }));
    const result = await httpFetch<{ data: undefined; status: 204; headers: Headers }>('/api/v1/x', { method: 'DELETE' });
    expect(result.data).toBeUndefined();
    expect(result.status).toBe(204);
    expect(typeof result.headers?.get).toBe('function');
  });

  it('never sends body or secret content to the console', async () => {
    const spy = vi.spyOn(console, 'log');
    vi.mocked(fetch).mockResolvedValue(
      new Response(JSON.stringify({}), { status: 200, headers: { 'Content-Type': 'application/json' } }),
    );
    await httpFetch('/api/v1/hosts', {
      method: 'POST',
      body: JSON.stringify({ idracPassword: 'super-secret', telegramBotToken: 't0ken' }),
    });
    expect(spy).not.toHaveBeenCalledWith(expect.stringContaining('super-secret'));
    expect(spy).not.toHaveBeenCalledWith(expect.stringContaining('t0ken'));
  });
});
