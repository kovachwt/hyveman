/** Vitest setup: jest-dom matchers, CSRF cookie, fetch mock helpers. */
import '@testing-library/jest-dom/vitest';
import { afterEach, beforeEach, vi } from 'vitest';
import { cleanup } from '@testing-library/react';

// jsdom lacks ResizeObserver (used by the ECharts wrapper).
class ResizeObserverStub {
  observe(): void {}
  unobserve(): void {}
  disconnect(): void {}
}
if (typeof globalThis.ResizeObserver === 'undefined') {
  globalThis.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver;
}

// jsdom lacks a canvas 2D implementation; ECharts/zrender needs one to lay
// out text. A minimal stub is enough for tests.
const noop = () => undefined;
const ctx2d = {
  canvas: null,
  measureText: (t: { toString(): string }) => ({ width: String(t).length * 7 }),
  fillRect: noop,
  clearRect: noop,
  getImageData: () => ({ data: new Uint8ClampedArray(4), width: 1, height: 1 }),
  putImageData: noop,
  createImageData: () => new Uint8ClampedArray(4),
  setTransform: noop,
  resetTransform: noop,
  drawImage: noop,
  save: noop,
  fillText: noop,
  strokeText: noop,
  restore: noop,
  beginPath: noop,
  moveTo: noop,
  lineTo: noop,
  closePath: noop,
  stroke: noop,
  clip: noop,
  fill: noop,
  arc: noop,
  translate: noop,
  scale: noop,
  rotate: noop,
  rect: noop,
  quadraticCurveTo: noop,
  bezierCurveTo: noop,
  isPointInPath: () => false,
  createLinearGradient: () => ({ addColorStop: noop }),
  createRadialGradient: () => ({ addColorStop: noop }),
  createPattern: () => null,
  setLineDash: noop,
  getLineDash: () => [],
  transform: noop,
  globalAlpha: 1,
  globalCompositeOperation: 'source-over',
  fillStyle: '#000',
  strokeStyle: '#000',
  lineWidth: 1,
  font: '10px sans-serif',
  textAlign: 'start',
  textBaseline: 'alphabetic',
  shadowBlur: 0,
  shadowColor: 'transparent',
  shadowOffsetX: 0,
  shadowOffsetY: 0,
  lineCap: 'butt',
  lineJoin: 'miter',
  miterLimit: 10,
  direction: 'inherit',
  filter: 'none',
};
if (typeof HTMLCanvasElement !== 'undefined') {
  // zrender only asks for 2d; other context types return null.
  HTMLCanvasElement.prototype.getContext = function (contextId: string) {
    if (contextId === '2d') return ctx2d as unknown as CanvasRenderingContext2D;
    return null;
  } as typeof HTMLCanvasElement.prototype.getContext;
}

// The API issues the CSRF cookie on any /api/v1 response; tests preset it so
// mutation requests don't trigger the extra ensure-cookie GET.
beforeEach(() => {
  document.cookie = 'hyveman_csrf=test-csrf-token; path=/';
  localStorage.clear();
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

export interface MockResponse {
  status?: number;
  /** JSON body; use a function to build it per-request. */
  body?: unknown | ((url: string, init: RequestInit) => unknown);
  headers?: Record<string, string>;
}

export type MockHandler =
  | { method?: string; path: string | RegExp; respond: MockResponse }
  | { method?: string; path: string | RegExp; respond: (url: string, init: RequestInit) => MockResponse };

export interface CapturedRequest {
  url: string;
  method: string;
  init: RequestInit;
  body: unknown;
}

const captured: CapturedRequest[] = [];

/** Installs a fetch stub backed by the given handlers. */
export function mockApi(handlers: MockHandler[]): { requests: () => CapturedRequest[]; reset: () => void } {
  captured.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method ?? 'GET').toUpperCase();
      captured.push({ url, method, init: init ?? {}, body: parseBody(init?.body) });
      for (const h of handlers) {
        const pathMatches = typeof h.path === 'string' ? url.includes(h.path) : h.path.test(url);
        if (!pathMatches) continue;
        if (h.method && h.method.toUpperCase() !== method) continue;
        const spec = typeof h.respond === 'function' ? h.respond(url, init ?? {}) : h.respond;
        const body = typeof spec.body === 'function' ? (spec.body as (u: string, i: RequestInit) => unknown)(url, init ?? {}) : spec.body;
        return jsonResponse(body, spec.status ?? 200, spec.headers);
      }
      return jsonResponse({ type: 'about:blank', title: 'Not mocked', status: 404, code: 'not_found' }, 404);
    }),
  );
  return {
    requests: () => [...captured],
    reset: () => {
      captured.length = 0;
    },
  };
}

function parseBody(body: BodyInit | null | undefined): unknown {
  if (body == null) return undefined;
  try {
    return JSON.parse(String(body));
  } catch {
    return String(body);
  }
}

export function jsonResponse(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers },
  });
}

/** Convenience: one handler per URL prefix with a body function. */
export function handler(
  method: string | undefined,
  path: string | RegExp,
  respond: MockResponse | ((url: string, init: RequestInit) => MockResponse),
): MockHandler {
  return { method, path, respond: respond as MockHandler['respond'] } as MockHandler;
}
