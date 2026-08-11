# Hyveman Web Frontend — Technical Design

**Status:** Draft implementation design  
**Application:** `hyveman-web`  
**Technology:** React + TypeScript + Vite

This document defines the standalone frontend for Hyveman. It consumes the
authenticated REST/JSON API described in [`API.md`](API.md). It does not speak
the agent protocol directly; only `hyveman-api` communicates with
`hyveman-agent` according to [`PROTOCOL.md`](PROTOCOL.md).

---

## 1. Goals and constraints

The frontend is an operations console for a small server fleet. Its primary
job is to make hardware health, agent state, events, alerts, and configuration
quick to understand and safe to operate.

Goals:

- show fleet health at a glance and make stale data obvious;
- provide efficient server-side event search over SQLite FTS5;
- support passkey-only login and first-run registration;
- expose alert acknowledgement, silencing, rule editing, and notification
  configuration;
- keep iDRAC and notification secrets out of the browser after submission;
- be independently built and deployed as static assets; and
- remain usable on a desktop operations workstation and a tablet-sized screen.

Non-goals:

- server-side rendering or SEO;
- direct access to SQLite or agent endpoints;
- local persistence of bearer tokens or credentials;
- client-side replacement of backend authorization or validation; and
- real-time streaming in the MVP. Polling is sufficient for the fleet size and
  hardware poll interval; SSE/WebSockets can be added later if needed.

---

## 2. Technology choices

| Area | Choice | Purpose |
|---|---|---|
| Runtime | React + TypeScript | Component UI with strong typing and a broad operations-dashboard ecosystem |
| Build | Vite | Fast development server and independent static production artifact |
| Routing | React Router | Public, setup, and authenticated application routes |
| API state | TanStack Query | Caching, polling, retries, invalidation, and stale-state handling |
| API client | Orval-generated client/hooks from ASP.NET OpenAPI | Keeps DTOs and query/mutation signatures synchronized with `hyveman-api` |
| Components | MUI | Accessible forms, data tables, dialogs, tabs, alerts, and theme support |
| Charts | Apache ECharts | Health history, temperature, power, and disk visualizations |
| Forms | React Hook Form + Zod | Local form state and immediate client-side feedback; API remains authoritative |
| Passkeys | `@simplewebauthn/browser` | Browser-side WebAuthn ceremonies |
| Unit/component tests | Vitest + Testing Library | Fast deterministic UI tests |
| Browser tests | Playwright | Route, authentication, workflow, and accessibility tests |

The frontend does not use Next.js, Nuxt, or another SSR framework in the MVP.
The application is authenticated, has no SEO requirement, and is best served as
static files.

---

## 3. Deployment and browser topology

The production artifact is the output of `vite build`, normally the `dist/`
directory. It is served by IIS, nginx, Caddy, or another static HTTP server.

Preferred public topology:

```text
https://hyveman.example.com/          -> hyveman-web static files
https://hyveman.example.com/api/...   -> reverse proxy to hyveman-api
```

This gives the browser one public origin, which simplifies:

- `HttpOnly; Secure; SameSite=Strict` session cookies;
- CSRF and `Origin` validation;
- WebAuthn expected-origin validation; and
- frontend API calls with `credentials: "include"`.

The frontend and API remain separate builds and processes. The reverse proxy is
only a deployment convenience; it does not make the frontend part of the .NET
process.

A separate API origin is supported but not preferred. In that arrangement the
API must allow only the exact configured frontend origin, enable credentialed
CORS, and use an explicit WebAuthn RP ID/expected origin matching the frontend.
The frontend must never work around this by storing a bearer session token in
`localStorage` or `sessionStorage`.

The static server must:

- serve `index.html` as the SPA fallback only for frontend routes;
- pass `/api/` to the API rather than returning the SPA fallback;
- pass `/register`, `/ingest/`, and `/health` to the API rather than returning
  the SPA fallback;
- avoid caching `index.html` for long periods; and
- serve hashed JavaScript/CSS assets with immutable caching.

---

## 4. Application structure

Recommended source layout:

```text
src/
  app/
    App.tsx
    router.tsx
    providers.tsx
    theme.ts
  api/
    generated/                 Orval output; never edit manually
    client.ts                  fetch mutator, credentials, CSRF, errors
    queryKeys.ts
  auth/
    AuthProvider.tsx
    authRoutes.tsx
    useSession.ts
    passkey.ts
  components/
    AppShell/
    DataTable/
    HealthBadge/
    HealthTile/
    LoadingState/
    ErrorState/
    EmptyState/
    ConfirmDialog/
    TimeDisplay/
  features/
    overview/
    hosts/
    events/
    saved-searches/
    alerts/
    rules/
    notifications/
    maintenance/
    sources/
    settings/
    audit/
    passkeys/
  pages/
    LoginPage.tsx
    SetupPage.tsx
    OverviewPage.tsx
    ...
  styles/
  main.tsx
```

Feature modules own their route components, query hooks, mutation hooks, form
schemas, and feature-specific presentation components. Shared components must
not contain feature-specific API calls.

The app has three providers:

1. `QueryClientProvider` for server state;
2. `AuthProvider` for the current session and route gating; and
3. `ThemeProvider` for the MUI theme and display preferences.

There is no Redux store in the MVP. Use:

- TanStack Query for server state;
- URL search parameters for shareable/search state;
- component state for dialogs, tabs, and transient UI state; and
- a small auth/theme context for cross-cutting state.

---

## 5. Routes and access control

The route table is:

| Route | Access | Purpose |
|---|---|---|
| `/login` | Public | Passkey login |
| `/setup` | Public but API-gated | First-run passkey registration (creates the first user) |
| `/accept-invite` | Public but API-gated | Invite-link account creation (token in URL fragment; docs/MULTI-USER.md) |
| `/` | Authenticated | Fleet overview |
| `/hosts` | Authenticated | Host list and filters |
| `/hosts/:hostId` | Authenticated | Host health, components, VMs, events, and history |
| `/hosts/:hostId/logons` | Authenticated | Per-user/per-day security-logon aggregates for the host |
| `/logs` | Authenticated | Event search and saved searches |
| `/alerts` | Authenticated | Active/history alert list and actions |
| `/rules` | Authenticated | Alert rule CRUD |
| `/notifications` | Authenticated | Notification channel configuration and tests |
| `/maintenance` | Authenticated | Maintenance windows |
| `/admin/users` | Authenticated | Users, invite links, invitations |
| `/admin/sources` | Authenticated | Sources, agent status, and registration tokens |
| `/admin/retention` | Authenticated | Retention settings |
| `/admin/audit` | Authenticated | Audit log |
| `/admin/passkeys` | Authenticated | The session user's passkey management ("My passkeys") |

`AuthProvider` performs a session bootstrap before rendering protected routes.
The possible states are:

```text
loading session -> setup required | unauthenticated | authenticated
```

Unauthenticated users are redirected to `/login`. If setup is required, the
app offers `/setup`; however, the API is the authority and can reject setup
requests from an untrusted network. A protected route must never be rendered as
if the user were authenticated merely because a previous query is cached.

After login, the app returns to the originally requested route when it is safe
to do so. Redirect URLs are internal paths only; the frontend does not accept
arbitrary external redirect targets.

---

## 6. API client and data fetching

### 6.1 Generated client

The build process obtains the `hyveman-api` OpenAPI document and runs Orval to
generate:

- TypeScript DTO types;
- request functions;
- TanStack Query query hooks; and
- TanStack Query mutation hooks.

Generated files live under `src/api/generated/` and are replaced during
regeneration. Handwritten code belongs in `src/api/client.ts` or feature
modules, not in generated files.

The client defaults to a relative base URL:

```text
/api/v1
```

A public, non-secret runtime configuration may override the API origin for
special deployments. The default same-origin path is preferred.

### 6.2 Request behavior

The fetch mutator:

- sends `Accept: application/json`;
- sends `Content-Type: application/json` for JSON bodies;
- uses `credentials: "include"`;
- adds the API-issued CSRF header for unsafe methods;
- aborts requests when the owning query is cancelled;
- parses Problem Details errors into a typed `ApiError`; and
- never logs request bodies that may contain secret fields.

GET requests do not need the CSRF header. Mutations must satisfy the API's
Origin and CSRF checks. The client should refresh the CSRF cookie/header state
as required by the API rather than inventing its own token format.

### 6.3 Query policy

Query keys include every server-side filter that changes the result. Examples:

```text
["overview"]
["hosts", filters]
["host", hostId]
["healthHistory", hostId, range, resolution]
["logonStats", hostId, range, user]
["events", eventFilters]
["alerts", alertFilters]
["rules"]
```

Recommended behavior:

- overview: refetch every 30 seconds while visible;
- host current state: refetch every 30–60 seconds;
- alert list: refetch every 15–30 seconds while the page is visible;
- event search: fetch on filter changes, with a 250–350 ms debounce for free
  text; and
- configuration pages: no aggressive polling, but refetch after mutations and
  on window focus.

These intervals are UI defaults, not correctness guarantees. Health age and
agent silence are calculated by the API and displayed using the API timestamps.
A browser that has been backgrounded must not make old cached green status look
current.

Mutation success normally invalidates affected queries rather than performing
optimistic updates. Acknowledge/silence actions may use optimistic UI only when
the previous state can be restored cleanly on failure.

---

## 7. Authentication and passkeys

### 7.1 Session bootstrap

On application start, call:

```text
GET /api/v1/auth/session
```

The response indicates whether the current browser is authenticated and whether
first-run setup is required. It includes the authenticated user's display
metadata (`user: {id, name, displayName}`), but no secret values.

The session is an opaque HttpOnly cookie issued by the API. The frontend never
reads, stores, decodes, or refreshes it manually.

### 7.2 Login

The login flow is:

1. call `POST /api/v1/auth/passkeys/login/options`;
2. pass the returned options to
   `startAuthentication` from `@simplewebauthn/browser`;
3. send the browser response to
   `POST /api/v1/auth/passkeys/login/verify`;
4. let the API set the session cookie; and
5. refetch `/auth/session` before entering the application.

The UI displays clear secure-context/browser-support errors and a retry action.
It does not fall back to passwords, TOTP, backup codes, or a frontend-managed
credential.

### 7.3 First-run registration

When the session response reports setup is required:

1. show `/setup` with the trusted-network warning;
2. call `POST /api/v1/auth/passkeys/register/options`;
3. pass options to `startRegistration`;
4. call `POST /api/v1/auth/passkeys/register/verify` (envelope body); and
5. refetch the session and enter the authenticated application.

The API can reject the request even if the route is visible. The frontend must
show that rejection without implying that setup succeeded.

### 7.4 Invite acceptance

The `/accept-invite` page (docs/MULTI-USER.md §7) reads the raw invite token
from the URL fragment (`#token=...` — never a query string, so it never
reaches server logs or Referer), optionally inspects it via
`POST /api/v1/auth/invitations/inspect` for a friendly banner, asks for a
username + passkey name, then runs the registration ceremony with
`inviteToken` in the options body and `{response, inviteToken, username,
displayName}` in the verify body. On success the API has created the account
and issued a session; the page refetches `/auth/session` and enters the app.

### 7.5 Passkey management

"My passkeys" (`/admin/passkeys`) lists only the **session user's** keys
(non-secret metadata: name, creation time, last-used time). New passkeys use
the same registration ceremony while authenticated and are added to the
session user's account. Removal requires confirmation; the API blocks removing
your last passkey and the final passkey of the last enabled user.

Logout calls `POST /api/v1/auth/logout`, clears the Query cache, resets the auth
context, and navigates to `/login`.

---

## 8. Feature designs

### 8.1 Overview dashboard

The overview page consumes the bounded `GET /api/v1/overview` response.
Each host tile shows:

- host name and kind;
- overall health with text, icon, and color;
- separate Hardware, OS, and Hyper-V summaries;
- agent status and age of the last heartbeat;
- iDRAC poll state from `poll_status` (API.md §9.1): last attempt time,
  OK/failed, and the failure reason in a tooltip — a host whose polls keep
  failing shows "Failed · time" with the error, never a permanent
  "never polled"; and
- active alert count.

The page includes summary counts for critical/warning hosts, silent agents, and
unacknowledged alerts. Clicking a tile navigates to `/hosts/:hostId`.

A stale-data banner appears when the API reports old snapshots or a query has
failed after previously succeeding. Green is never shown without an associated
last-updated time.

### 8.2 Host details

The host page is tabbed or sectioned into:

- summary and current rollup;
- component health table for CPUs, DIMMs, disks, controllers, PSUs, fans, and
  temperatures;
- health-history charts using server-bucketed snapshots/metrics;
- VM list with state, heartbeat, replication health, CPU, memory, and
  last-seen time;
- recent critical events; and
- host-scoped alerts and maintenance windows.

The Hardware (iDRAC) panel shows the URL, credential state, the last poll
outcome (OK/failed with time and the failure reason), and whether an
accepted-on-first-use certificate pin is active, with a Clear action that
calls `DELETE /api/v1/hosts/{id}/idrac-cert` so the next poll can re-accept
a rotated certificate (API.md §9.1).

Charts display the requested range and resolution returned by the API. The
frontend does not request unbounded history and does not attempt to aggregate
raw samples in the browser.

Component detail is shown as escaped text. Raw Redfish or event payloads are
available only through an explicit detail view and remain text, not HTML.

### 8.3 Event search

The log page stores filters in the URL so searches can be bookmarked or shared
within the admin UI. Filters include:

- time range;
- host/source;
- channel;
- minimum severity;
- event ID; and
- free-text query.

The search form validates ranges locally, updates the URL, debounces free text,
and requests the first cursor page. Results use a dense, virtualized table for
large pages. Columns include time, host, channel, severity, provider, event ID,
and message preview.

Selecting a row opens a detail panel with all indexed fields, structured event
data, and escaped raw content. The browser does not use `dangerouslySetInnerHTML`
for event or API-provided content.

Saved searches are ordinary API resources. Saving a search serializes the
current normalized filter state, not the rendered table or browser-specific
state.

### 8.4 Alerts and rules

The alert page separates active, acknowledged, silenced, and historical states.
Each row shows rule, host, severity, first/last seen, count, and current status.
Acknowledge and silence actions require a confirmation/reason where configured
and immediately invalidate the alert query.

Rule forms are type-specific:

- health state rules expose component/state selectors;
- event rules expose source/channel/event ID/severity/message matching;
- heartbeat rules expose duration and source scope;
- threshold rules expose metric, comparator, value, and duration;
- VM-heartbeat rules have no options: they fire when a running VM with a
  prior OK heartbeat goes lost, and resolve on recovery or power-off;
- VM-replication rules expose replication-health and replication-state
  selectors (default: fire when `replication_health` is warning or critical),
  and resolve when replication returns to a non-matching state; and
- user-logon rules expose an outcome (success/failure/lockout) and an
  optional comma-separated user list (empty = any user); the UI notes that
  `DWM-x`/`UMFD-x` internal accounts are ignored for any-user rules.

Every form exposes a cooldown and an optional **auto-resolve after** field
(seconds; blank or 0 = never): event/logon rules fire-and-bump without a
natural resolution, so an auto-resolve timeout makes the alert close itself
once the condition goes quiet — no manual acknowledgement needed.

The UI provides human-readable summaries but submits the typed match document
expected by the API. Client-side schemas improve feedback; the backend remains
the authority.

### 8.5 Notification channels

The channel list shows name, type, enabled state, creation/rotation metadata,
and last test status. Secret fields are write-only:

- blank on edit means "leave current value unchanged";
- a newly entered value is sent only over HTTPS; and
- the response never echoes it.

A test action uses a confirmation dialog and displays success/failure without
exposing provider response bodies that may contain URLs or credentials.

### 8.6 Administration

The admin area includes:

- **users** (`/admin/users`): user list (name, display name, passkey count,
  last active, disabled state) with disable/enable/delete — the API enforces
  self- and last-user guards; invite-link creation showing the raw link
  **once** with a copy action (the token lives in the URL fragment, exactly
  like the `reg_` token discipline); pending invitation list with revoke;
  per-user passkey removal for lost-device recovery;
- source and host registration, including iDRAC URL and write-only iDRAC credentials;
- one-time agent registration-token creation;
- token revocation and last-used metadata;
- retention settings;
- maintenance windows;
- audit history (actor column shows real usernames); and
- "My passkeys" (the session user's keys).

A raw `reg_` token is displayed once with a clear warning and a copy button. It
is not placed in the URL, query string, analytics event, browser local storage,
or application logs. The API remains responsible for expiration, binding, and
single-use enforcement.

Host create/edit forms accept the iDRAC URL and iDRAC credentials as
write-only fields, with the same rules as notification secrets (§8.5):
username and password are both required when setting them, blank on edit
means "leave the stored value unchanged", and responses expose only a
credential-set flag — never the value. Entered secrets are cleared after
submission and never placed in the URL, browser storage, or application logs.

### 8.7 Logon stats

The logon-stats view answers "who logged on where, how often" per host. It
consumes `GET /api/v1/logon-stats` with time-range, source, and user filters.
The API returns per-user/per-day rows with success and failure counts and a
`hasMore` flag; there is no cursor, so when more rows are available the view
offers "narrow the filters or raise the page size" (the API caps page size at
200). Filters persist in the URL (same pattern as §8.3), and the source and
user filters are exact matches.

The aggregates are derived server-side from curated Security events (4624
interactive/RDP successes, 4625 failures, 4740 lockouts), so the view is
read-only and never re-aggregates events in the browser. Because the API
filters by source, the host page resolves the host's associated source before
querying; a host without an associated source returns no rows.

Days are UTC calendar days (`yyyy-MM-dd`) and are labeled as UTC to avoid
off-by-one ambiguity. Lockout rows have no logon type. Presentation combines a
summary strip (successes, failures, lockouts), a dense table (day, user, logon
type with Interactive/RDP labels, success, failure), and ECharts
visualizations such as a stacked per-day success/failure bar chart.

## 9. Visual system and accessibility

The UI is desktop-first but responsive enough for a tablet. Use MUI theme
variants for light/dark display without changing the meaning of health states.

Health states must not rely on color alone:

| State | Visual treatment |
|---|---|
| OK | green accent + `OK` label/check icon |
| Warning | amber accent + `Warning` label/triangle icon |
| Critical | red accent + `Critical` label/error icon |
| Unknown/stale | neutral accent + `Unknown`/`Stale` label |

Requirements:

- WCAG AA contrast for text and status indicators;
- keyboard-accessible navigation, dialogs, tables, and action menus;
- visible focus indicators;
- labels and accessible names for icon-only buttons;
- semantic headings and landmarks;
- no flashing or animation for critical alerts;
- tooltips or accessible text for abbreviated hardware names; and
- responsive tables that preserve the most important columns first.

Times are displayed in the browser's local timezone with a UTC tooltip or
secondary representation. Relative times such as “2 minutes ago” must include
an absolute timestamp for precision.

---

## 10. Loading, error, and empty states

Every query-driven view defines all four states:

1. initial loading;
2. loaded data;
3. empty result; and
4. error, including an error after previously loaded data.

When a refetch fails after data was shown:

- retain the last successful data;
- mark it visibly stale;
- show the error and retry action; and
- do not silently reset the view to an empty or healthy state.

For mutations, disable duplicate submission while pending, show the API's
stable error code in a user-friendly message, and preserve entered non-secret
form values where safe. Secret fields are cleared after a successful submission
and not restored after an error unless the user re-enters them.

Network loss should produce a non-blocking connection banner. The app remains
read-only against cached data, but it must label that data as stale and prevent
mutations from appearing successful without an API response.

---

## 11. Security requirements

- Serve only over HTTPS in any non-local development environment.
- Use the API session cookie; never store auth tokens or secrets in web storage.
- Send credentials only to the configured API origin.
- Honor the API's CSRF and exact-origin requirements for every mutation.
- Do not render event messages, raw XML, Redfish details, or API error text as
  trusted HTML.
- Do not log passkey responses, session cookies, registration tokens, or secret
  form values to the browser console or telemetry.
- Apply a restrictive CSP that allows only the app's own scripts/styles and the
  required API origin. Avoid inline script unless a deployment-specific nonce
  is provided.
- Keep source maps out of public production hosting unless access-controlled.
- Pin/audit dependencies during CI and review WebAuthn/browser library updates.
- Ensure clipboard helpers clear or stop referencing a registration token after
  the user leaves the page; the clipboard itself is controlled by the OS.

Frontend security is defense in depth. Authorization, setup-network checks,
secret storage, WebAuthn validation, CSRF validation, and audit records are
server responsibilities.

---

## 12. Performance and compatibility

The expected fleet is small, but event result sets can be large. The frontend
must:

- use server-side event filtering and cursor pagination;
- virtualize large event/component tables;
- lazy-load route-level feature bundles where practical;
- avoid refetching the same host data independently from every child component;
- request bounded chart ranges/resolutions;
- cancel obsolete search requests; and
- avoid rendering full raw payloads until the user opens the detail view.

The initial bundle should not include every admin feature if route-level code
splitting can remove it. The dashboard path should load independently of the
large event-search and charting code where practical.

Support the currently maintained desktop versions of Chromium-based browsers,
Firefox, and Safari that provide WebAuthn/passkey support. The setup and login
pages must report unsupported browser/security-context conditions clearly.

---

## 13. Build and release

CI should run:

```text
npm ci
npm run lint
npm run typecheck
npm run test -- --run
npm run build
```

The build should fail if the generated OpenAPI client is stale. A recommended
sequence is:

1. fetch the pinned API OpenAPI document;
2. regenerate `src/api/generated/`;
3. fail on a dirty generated diff;
4. run TypeScript and tests; and
5. build the static artifact.

The artifact should contain:

- hashed JS/CSS chunks;
- `index.html` with the build version;
- non-secret runtime configuration only; and
- no source maps or development endpoints by default.

Cache policy:

- `index.html`: revalidate/no long-lived cache;
- hashed assets: long-lived immutable cache; and
- API responses: controlled by the API and query library, not static asset
  caching.

The frontend release includes a build identifier displayed in the admin/about
area and sent as a harmless `User-Agent` or header if useful for diagnostics.

The API is versioned independently. Additive API changes are preferred. A
breaking web API change requires a new API version or a compatibility period so
an already-deployed static frontend does not fail immediately after an API
upgrade.

---

## 14. Testing strategy

### Unit tests

Test:

- status/severity formatting and stale-state logic;
- query-string serialization/deserialization for event filters;
- logon-stats filter serialization, UTC-day handling, and count formatting;
- form schemas and validation messages;
- alert/rule form transformations;
- API error mapping;
- route guard decisions; and
- secret-field redaction behavior.

### Component tests

Using Testing Library and mocked API responses, cover:

- overview tiles and stale banners;
- host component tables and health charts;
- event filtering, cursor navigation, and detail rendering;
- logon stats table, UTC-day labels, and the bounded-result notice;
- alert acknowledgement/silence flows;
- notification secret create/edit behavior; and
- loading, empty, error, and retry states.

### Browser tests

Playwright scenarios should include:

1. first-run setup with a virtual WebAuthn authenticator;
2. passkey login and logout;
3. redirect from a protected route to login and back;
4. overview to host detail navigation;
5. event search, URL persistence, and saved search creation;
6. alert acknowledgement and maintenance-window creation;
7. notification channel creation without secret echoing; and
8. token creation showing the raw registration token only once.

Use a test API or controlled test environment. Do not use production
credentials, real Telegram tokens, real webhook URLs, or real iDRAC secrets in
frontend tests.

Run an accessibility scan on the primary routes and keyboard-test dialogs,
menus, tables, and passkey actions.

---

## 15. Implementation order

1. Create the Vite/React/TypeScript app, MUI theme, routing shell, linting, and
   CI checks.
2. Generate the API client from the first OpenAPI document and implement the
   fetch mutator, Problem Details mapping, query client, and CSRF handling.
3. Implement session bootstrap, login, setup, logout, route guards, and
   passkey management.
4. Implement the overview, host list/detail, current health, VM views, and logon stats.
5. Implement event search, detail rendering, cursor pagination, and saved
   searches.
6. Implement alerts, acknowledgement/silence, rules, and maintenance windows.
7. Implement notification channels, sources, registration tokens, retention,
   audit, and remaining admin pages.
8. Add chart optimization, virtualization, route-level code splitting,
   accessibility hardening, and browser workflows.
9. Add production headers, cache rules, deployment configuration, and restore/
   rollback documentation.

The frontend should be usable against a mocked API early, but every production
request and response must come from the generated `hyveman-api` contract. The
frontend never reaches around that contract to access agent data or storage.
