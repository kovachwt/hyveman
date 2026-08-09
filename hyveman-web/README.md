# hyveman-web

The Hyveman operations console — a React + TypeScript + Vite single-page
application consuming the authenticated REST/JSON API of `hyveman-api`
(`/api/v1`). It never speaks the agent protocol and never touches SQLite or
agent endpoints directly.

| Doc | Role |
|---|---|
| [`docs/FRONTEND.md`](../docs/FRONTEND.md) | This frontend's contract (authoritative) |
| [`docs/API.md`](../docs/API.md) | Backend API design — the OpenAPI source |
| [`docs/PROTOCOL.md`](../docs/PROTOCOL.md) | Agent wire protocol (not used by this app) |

## Stack

React 19, TypeScript, Vite 7, MUI 7, TanStack Query, React Router 7, ECharts,
React Hook Form + Zod, `@simplewebauthn/browser` (passkeys), Orval-generated
API client, Vitest + Testing Library, Playwright.

## Layout

```
src/
  api/            generated client (never edit), fetch mutator, query keys
  auth/           session bootstrap, route guards, passkey ceremonies
  components/     shared UI (AppShell, DataTable, HealthBadge, Chart, …)
  features/       per-feature pages + hooks + form schemas
  pages/          LoginPage, SetupPage
  lib/            pure formatting/health helpers (unit-tested)
  test/           vitest setup + fetch mock helpers
e2e/              Playwright browser tests (mock API + virtual WebAuthn)
openapi/          pinned hyveman-api OpenAPI document (client input)
scripts/          openapi fetch, Playwright mock API
```

## Development

```bash
npm ci
npm run dev          # http://localhost:5173, proxies /api to 127.0.0.1:5080
```

Point the dev proxy at a different API with `HYVEMAN_API_PROXY`:

```bash
HYVEMAN_API_PROXY=http://127.0.0.1:5080 npm run dev
```

The dev server relaxes the strict production CSP (Vite's inline react-refresh
preamble); the built artifact keeps the strict policy from `index.html`.

## Regenerating the API client

1. Start `hyveman-api` in Development (`dotnet run --project src/Hyveman.Api
   -- --WebAuthnRpId=localhost --WebAuthnExpectedOrigin=http://localhost:5080`).
2. `npm run api:fetch` — pins the OpenAPI document into `openapi/openapi.json`
   (absolute server URLs stripped so the client always uses the relative
   `/api/v1` base).
3. `npm run api:generate` — Orval writes `src/api/generated/endpoints.ts`.
4. `npm run api:check` — regenerates and fails on a dirty generated diff
   (used in CI so a stale client breaks the build).

## Checks (CI)

```bash
npm ci
npm run lint
npm run typecheck
npm run test -- --run     # 107 unit/component tests
npm run build             # static artifact in dist/
```

## Browser tests

```bash
npx playwright install chromium        # with system deps: --with-deps
npm run e2e
```

Playwright starts a bundled mock API (`scripts/mock-api.mjs`, port 5099) and
the Vite dev server proxying to it. Passkey ceremonies use a **CDP virtual
WebAuthn authenticator** (resident keys are not enumerated by the virtual
authenticator, so the mock echoes the registered credential id in login
options). No production credentials, Telegram tokens, webhook URLs, or iDRAC
secrets are used anywhere in the tests.

The tests exercise: first-run setup, passkey login + redirect-back, logout,
overview → host detail navigation (components/history/VMs), event search with
URL persistence and the raw-payload detail panel, and alert acknowledgement
with a required reason.

## Deployment

`npm run build` produces `dist/`. Serve it as static files behind the same
public origin as the API (see `docs/FRONTEND.md` §3 for the recommended
topology and cache policy):

- `index.html` → short revalidation, never long-lived cache;
- hashed `/assets/*` → immutable, long-lived cache;
- `/api/`, `/register`, `/ingest/`, `/health` → reverse proxy to hyveman-api;
- SPA fallback only for frontend routes.

## Security notes

- Session cookie is HttpOnly/Secure/SameSite=Strict and is only ever touched
  by the API; the frontend never stores tokens or secrets in web storage.
- Unsafe requests carry the API-issued CSRF header/cookie pair.
- iDRAC credentials, notification secrets, and registration tokens are
  write-only in the UI: blank-on-edit means unchanged, values are cleared
  after submission, and a `reg_` token is shown exactly once with a copy
  button, never placed in the URL or storage.
- Event payloads, raw XML, and API error text are always rendered as escaped
  text — never `dangerouslySetInnerHTML`.
- The production CSP blocks inline scripts; no source maps are emitted by
  default.
