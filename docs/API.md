# Hyveman API Server — Technical Design

**Status:** Draft implementation design  
**Runtime:** .NET 10 / ASP.NET Core  
**Executable:** `hyveman-api`

This document defines the implementation design for the Hyveman backend API. It
implements the agent boundary specified by [`PROTOCOL.md`](PROTOCOL.md) and
provides the separate HTTP API consumed by [`FRONTEND.md`](FRONTEND.md).
[`DESIGN.md`](DESIGN.md) remains the system-level contract.

The protocol document uses the historical name `hyveman-server` in a few places;
that name means `hyveman-api` in this document. The agent wire protocol and the
web/admin API are deliberately separate contracts.

---

## 1. Responsibilities

`hyveman-api` is a modular monolith. It is one deployable process, but its
responsibilities are separated behind application interfaces so the storage
provider, hardware provider, and notification providers can evolve
independently.

It is responsible for:

- accepting and authenticating agent registration, log, telemetry, and health
  requests exactly as specified in `PROTOCOL.md`;
- storing events, current agent/VM state, hardware health, metrics, alerts, and
  configuration in SQLite;
- polling Dell iDRAC Redfish endpoints and normalizing results into the
  vendor-neutral health model;
- evaluating health, event, threshold, and heartbeat rules;
- delivering notifications through Telegram, generic webhooks, and later SMTP;
- exposing the authenticated REST/JSON API used by `hyveman-web`;
- managing passkey ceremonies and the single-admin session cookie;
- maintaining retention, audit, notification outbox, and backup jobs; and
- providing readiness, structured logging, and operational diagnostics.

It is **not** responsible for:

- collecting Windows Event Logs directly;
- serving the React application from the .NET process;
- opening inbound connections to agents;
- storing plaintext credentials; or
- executing agent commands in v1. The command response slot is reserved by
  `PROTOCOL.md` but remains disabled.

---

## 2. Runtime architecture

```text
                    HTTPS
  Windows agents ───────────────┐
                                 ▼
                     ┌─────────────────────┐
                     │ ASP.NET Core host   │
                     │                     │
                     │ Agent protocol     │─── /register
                     │ endpoints          │─── /ingest/logs
                     │                     │─── /ingest/telemetry
                     │                     │─── /health
                     │                     │
                     │ Web REST API       │─── /api/v1/*
                     │ Passkey/session    │
                     │                     │
                     │ Hosted workers      │
                     │  - Redfish         │
                     │  - alerts          │
                     │  - notifications   │
                     │  - retention       │
                     │  - backups         │
                     └──────────┬──────────┘
                                │
                         SQLite data directory
                                │
               ┌────────────────┼────────────────┐
               ▼                ▼                ▼
          iDRAC Redfish     Telegram/webhooks   static web
                                                frontend via
                                                reverse proxy
```

The process can run as:

- a Windows service on Windows Server 2019+;
- a systemd service on Linux; or
- a console process in Docker or another supervisor.

The API should normally sit behind IIS, nginx, Caddy, or an equivalent reverse
proxy that terminates the public certificate and routes the static frontend and
`/api` to the appropriate process. The preferred public topology is one origin:

```text
https://hyveman.example.com/          -> hyveman-web static files
https://hyveman.example.com/api/...   -> hyveman-api web API
https://hyveman.example.com/register  -> hyveman-api agent protocol
```

The agent endpoint paths must remain the paths defined by `PROTOCOL.md`; they
must not be renamed to fit the web API prefix.

---

## 3. Project and module structure

A recommended .NET solution layout is:

```text
src/
  Hyveman.Api/                    ASP.NET Core host, routing, middleware
  Hyveman.Application/            use cases, ports, orchestration
  Hyveman.Domain/                 entities, value objects, rule semantics
  Hyveman.Protocol/               agent DTOs, validators, protocol responses
  Hyveman.Contracts/              web API DTOs and OpenAPI-facing models
  Hyveman.Infrastructure.Sqlite/  repositories, migrations, FTS5, transactions
  Hyveman.Infrastructure.Redfish/ Dell Redfish provider
  Hyveman.Infrastructure.Security/ tokens, vault, passkeys, sessions
  Hyveman.Infrastructure.Notify/  Telegram, webhook, notification outbox

tests/
  Hyveman.Protocol.Tests/
  Hyveman.Application.Tests/
  Hyveman.Infrastructure.Tests/
  Hyveman.Api.Tests/
  Hyveman.Contract.Tests/
```

This is a modular monolith, not a collection of networked microservices. The
application layer depends on interfaces such as:

```text
IEventStore
ITelemetryStore
IHealthStore
IAlertStore
IRuleStore
ISourceStore
ITokenStore
ICredentialVault
IHardwareProvider
INotifier
IBackupStore
```

Infrastructure supplies those implementations. HTTP handlers should call
application services rather than repositories directly. This keeps protocol
validation, web authorization, background jobs, and future storage changes
from duplicating business rules.

The agent protocol DTOs are owned by the API implementation, but their behavior
is tested against the examples and invariants in `PROTOCOL.md`. A shared binary
contract with the Windows agent is not required; the document is the authority.

---

## 4. HTTP surfaces and contract boundaries

### 4.1 Agent protocol

These routes implement `PROTOCOL.md` v1 exactly:

| Method | Path | Authentication | Purpose |
|---|---|---|---|
| `POST` | `/register` | `reg_` bearer token | Exchange one-time enrollment token for an `agt_` token |
| `POST` | `/ingest/logs` | `agt_` token, `ingest` scope | Idempotent event batches |
| `POST` | `/ingest/telemetry` | `agt_` token, `ingest` scope | Latest-wins heartbeat and VM facts |
| `GET` | `/health` | Optional `agt_` token | Connectivity and optional token inspection |

Agent requests carry `X-Hyveman-Protocol` (including `GET /health`) and
JSON requests also carry a matching top-level `v`. Agent responses use the
protocol `v` field, `X-Hyveman-Protocol` response header, and reserved
`commands` array. They do not use the web API envelope. For a missing,
unsupported, or mismatched protocol version, the response uses the server's
current protocol version and includes `error.supported` where specified by
`PROTOCOL.md`; it must not echo an unsupported client version.

### 4.2 Web/admin API

The frontend API is versioned in its URL:

```text
/api/v1/...
```

It uses the browser session cookie, not agent bearer tokens. It is documented by
ASP.NET Core OpenAPI and is the only contract the React frontend consumes. Agent
registration tokens may be created by the web API, but the raw token is returned
only at creation time.

The web API must never expose:

- agent bearer tokens after their creation response;
- iDRAC credentials;
- Telegram bot tokens;
- webhook URLs or SMTP credentials; or
- the vault encryption key.

---

## 5. Common API implementation rules

### 5.1 Agent protocol pipeline

Agent routes use a dedicated pipeline with the following order:

1. assign a correlation/trace ID;
2. enforce HTTPS and reject plain HTTP;
3. require and parse `X-Hyveman-Protocol` before deserializing the body or
   authenticating: an absent header is `400 missing_version`, an unsupported
   header is `400 unsupported_version`, and both responses use the server's
   current version plus `error.supported`;
4. inspect `Content-Encoding` and accept only identity/absent or gzip, then
   decompress while enforcing the **4 MiB decompressed, reassembled** body
   limit (including chunked requests);
5. deserialize JSON and validate the body `v`: a present value different from
   the supported header is `400 invalid_request`; a body `v` never substitutes
   for the required header;
6. authenticate the bearer token and resolve its source/scope (with optional
   token introspection for `/health`);
7. apply source/global rate limits;
8. validate the endpoint-specific request and source-kind semantics; and
9. execute the application command and produce the protocol response.

The version checks in step 3 have precedence over body-version and endpoint
validation. A server-generated protocol response always carries the server's
current `X-Hyveman-Protocol` and JSON `v` for a version error. All server-
generated 2xx and error envelopes include `commands`; reverse-proxy-generated
errors may lack the JSON envelope and are classified by HTTP status by agents.

All protocol failures use the error codes and retry semantics from `PROTOCOL.md`.
Unknown optional JSON fields are ignored for forward compatibility. Required
fields, limits, source kind, item kinds, and enum values are validated
explicitly, with endpoint-specific semantic checks performed in the
application service rather than by headers-only middleware.

### 5.2 Web API conventions

- JSON property names are camelCase.
- Web API timestamps are UTC RFC 3339 strings with an explicit `Z` or offset;
  the application normalizes them to UTC before persistence. This convenience
  does not loosen the agent protocol: protocol body timestamps are validated as
  UTC strings with a trailing `Z` by the protocol DTO/schema.
- IDs are opaque strings to the client. The frontend must not infer database
  types or parse ID formats.
- Collection responses use an `items` array and, where applicable, `nextCursor`
  and `hasMore` fields.
- Log search is cursor-paginated. The maximum page size is 200; the default is
  50. Deep offset pagination is not exposed.
- Mutating requests return the updated resource where useful. Deletes and
  action endpoints return `204` or a small action result.
- Concurrent updates to rules, host metadata, channels, and maintenance
  windows use an `updatedAt`/version check and return `409 conflict` when the
  client is stale.
- Web API errors use RFC 9457 Problem Details with an additional stable
  `code`, `traceId`, and field-level `errors` object where applicable:

```json
{
  "type": "https://hyveman.example/errors/validation",
  "title": "Validation failed",
  "status": 400,
  "code": "validation_failed",
  "detail": "One or more fields are invalid.",
  "traceId": "...",
  "errors": { "name": ["Name is required."] }
}
```

The frontend branches on `status` and `code`, never on the human-readable
`detail` text.

---

## 6. Agent protocol implementation

### 6.1 Token authentication

Tokens are generated using a cryptographically secure random generator and are
returned only over the registration response or the administrator's one-time
registration-token response. The database stores a hash and token metadata,
never the raw value.

The authentication service:

- parses the `reg_` or `agt_` prefix before database lookup;
- hashes the complete raw token and performs a constant-time lookup/comparison;
- resolves exactly one `source_id` and its scopes;
- updates `last_used` asynchronously or in the same small transaction; and
- distinguishes invalid, revoked, consumed, missing, and wrong-scope cases
  according to `PROTOCOL.md`.

The request body `source`, `X-Hyveman-Source` header, and telemetry
heartbeat `source_id` are corroborating values only. The authentication result
always supplies the authoritative `source_id`; the API logs a warning on a
mismatch but never changes identity or persistence routing based on a hint.
The source header is omitted on `/register`, before a source exists.

### 6.2 Registration

`RegistrationService` handles `POST /register` in one transaction:

1. validate the protocol version and `reg_` token;
2. verify the token is bound to the requested source kind and is unused;
3. resolve or create the `(kind, hostname)` source;
4. generate and hash a new `agt_` token;
5. insert the token with the `ingest` scope;
6. mark the registration token consumed; and
7. commit before returning the token, `source_id`, scopes, `issued_at`, and
   `commands`.

The database requires `UNIQUE(kind, name)` and the transaction prevents two
concurrent registrations from consuming the same registration token or
creating duplicate source rows. In v1 the tuple is authoritative: a second
physical host with the same kind and hostname reuses the existing source; the
API must not auto-disambiguate it or emit `name_collision`. `boot_id` is
informational, per-boot data and is never used for source resolution. The
reinstall path issues a fresh agent token for the reused source. The old token
is not silently revoked; the administrator can revoke it from the web UI.

The transaction commits before the response is written. If the response is
lost after commit, a retry with the one-time token correctly returns
`410 token_consumed`; there is no implicit second token. The operator issues a
fresh registration token, and the same `(kind, hostname)` lookup preserves the
source ID. This is the documented response-loss recovery path and should be a
clear diagnostic rather than an unhandled startup exception.

### 6.3 Log ingestion

`LogIngestService` processes `/ingest/logs` as follows:

1. enforce the 4 MiB decompressed request and 1000-item limits;
2. validate the envelope and each item, returning the documented permanent
   per-item rejection reason for an invalid item;
3. derive `source_id` from the token and warn, without trusting, mismatched
   body/header source hints;
4. validate severity/facility using the authenticated source kind: Windows
   levels are 1–5 (or omitted when native Level is 0), while the future
   `syslog-feed` uses RFC 5424 severity 0–7 and an opaque facility string;
5. map promoted Windows fields (`channel`, `event_id`, `task`, `opcode`,
   `keywords`) into indexed event columns;
6. insert valid items using the unique key
   `(source_id, dedup_scope, record_id)`;
7. update FTS5 only for newly inserted messages; and
8. return `accepted`, `deduped`, and permanent per-item rejections.

A malformed item does not reject the other valid items in the batch. The
service must not reject unknown optional fields merely because they are absent
from the current DTO; additive fields are ignored or retained in the JSON
fields object according to the protocol. A database or infrastructure failure
rejects the whole request with a retryable 5xx; the agent retains the spool
file. `400 too_many_items` and `413 payload_too_large` are the two special
whole-batch errors that trigger agent-side recursive splitting rather than
quarantine. The invariant

```text
accepted + deduped + rejected.length == items.length
```

must hold for every successful log response.

The insert is equivalent to:

```sql
INSERT INTO events (...)
VALUES (...)
ON CONFLICT(source_id, dedup_scope, record_id) DO NOTHING;
```

The service does not parse `record_id`; epoch-prefixed values remain opaque
strings. It does normalize and validate that `dedup_scope` is non-null and
`record_id` is non-empty and at most 128 characters.

### 6.4 Telemetry ingestion

`TelemetryService` handles `/ingest/telemetry` without a spool or idempotency
key. It applies the protocol's ordering rules rather than treating arrival
order as latest-wins:

- a heartbeat replaces stored heartbeat state when there is no prior state,
  the incoming `boot_time` differs from the stored boot session, or its
  `sent_at` is newer; an older `sent_at` in the same boot session is ignored;
- `received_at` is captured from the server clock independently of `sent_at`,
  and the heartbeat-silence timer uses that receive time. A valid heartbeat
  arrival can therefore keep the source live even when its state payload is
  older and is not stored;
- the latest agent version, OS build, boot time, counters, degraded state, and
  config hash are retained with the accepted heartbeat state;
- a facts snapshot replaces the stored snapshot only when `collected_at` is
  newer. Multiple facts items in one request are applied in array order under
  the same rule;
- `stale:true` facts are still valid snapshots and may replace an older
  snapshot, but the stale condition is retained for UI/alerting. `vms: []`
  with `stale:false` means the scan succeeded and the host has no VMs; it is
  not a scan failure; and
- the heartbeat monitor is notified after processing so an agent-silent alert
  can clear or recover.

The heartbeat item's `source_id`, like the envelope `source` hint, is never an
identity source. The service rejects malformed telemetry as a whole 4xx
request. A valid but older payload still receives HTTP 200 with
`{"v":1,"accepted":true,"commands":[]}`; telemetry has no per-item result.
Historical heartbeat
samples are optional and are not required for MVP dashboards.

### 6.5 Health endpoint

`GET /health` is intentionally separate from operational readiness endpoints.
It always returns the protocol connectivity response when the process is
reachable and healthy, even if an optional token is missing or invalid. Token
presence is reported only through the presence of `source_id` and `scopes`, as
specified by `PROTOCOL.md`. A healthy response is `200` with `v`, `ok`, server
time/version, and `commands`; an unready process returns the protocol
`503 unavailable` response. This endpoint's lenient token behavior must not be
reused for the required-auth ingest endpoints.

Additional non-agent endpoints may be provided for infrastructure monitoring:

- `/health/live` — process is running;
- `/health/ready` — database and required dependencies are ready; and
- `/metrics` — optional future Prometheus endpoint.

These endpoints do not change the `/health` wire contract.

### 6.6 Limits and rate limiting

Use ASP.NET Core request limits plus an explicit decompression counter so a
compressed request cannot bypass the 4 MiB protocol limit. Count the complete
reassembled identity JSON for chunked requests, not just `Content-Length` or
compressed bytes. Apply the protocol's per-item caps as well: 1000 items,
16 KiB server hard cap for `raw`, 64 KiB for `message` and string `fields.*`
values, and 128 characters for `record_id`. The agent's 8 KiB raw truncation
is an optimization, not a server-side exemption.

Accept only absent/identity or gzip `Content-Encoding`; return
`unsupported_media_type` (or the permitted `invalid_request`) for other
encodings. Apply:

- a global request budget;
- a per-source budget keyed by authenticated `source_id`;
- a registration budget keyed by source/network; and
- stricter limits to unauthenticated web authentication endpoints.

The API returns `Retry-After` on `429 too_many_requests` and `503 unavailable`
as specified by the protocol. The agent, not the server, caps an advertised
wait at 3600 seconds. Logging must record rate-limit events without logging
authorization headers or raw payloads.

### 6.7 Machine-readable protocol schema

`schemas/protocol-v1.json` is the draft-07 structural schema for the v1 JSON
request and response bodies. It is deliberately separate from OpenAPI: it
covers the agent protocol's `oneOf` body shapes, while headers, TLS,
authorization, decompressed byte limits, token-derived identity, and
latest-wins ordering remain application concerns. The API should use it as a
structural check and then run the endpoint/token/source-kind validators
explicitly. In particular, the schema cannot decide whether a severity is
valid without the authenticated source kind, and it cannot implement the
heartbeat/facts ordering rule.

The protocol's additive compatibility rule is binding: unknown optional JSON
members must be ignored (or preserved in `fields` where applicable), not
turned into a 4xx solely because the current DTO or schema fixture does not
name them. Schema validation must therefore be run in a forward-compatible
mode; `PROTOCOL.md` wins over any stricter schema detail.

### 6.8 Contract tests

The API test suite must include golden request/response fixtures for:

- registration and token consumption;
- missing, unsupported, and mismatched protocol versions;
- invalid, revoked, and wrong-scope tokens;
- duplicate log records and epoch-prefixed record IDs;
- mixed valid/invalid log batches;
- gzip and body-size limits;
- latest-wins telemetry;
- optional-token health checks; and
- all protocol error codes and required response fields, including
  `commands` on success/error envelopes and the server-version headers on
  version errors;
- schema validation for every request/response body shape, with unknown
  optional members retained as forward-compatible fixtures; and
- heartbeat reordering by `boot_time`/`sent_at`, facts reordering by
  `collected_at`, stale snapshots, and an explicitly empty (successful) VM
  list.

These tests are compatibility tests, not merely controller unit tests. A
protocol change requires updating `PROTOCOL.md` and its versioning decision
before implementation.

---

## 7. Web/admin API

The web API is intentionally not part of the agent protocol. Its initial
resource surface is:

| Area | Endpoints | Notes |
|---|---|---|
| Session | `GET /api/v1/auth/session`, login/register options and verification, logout | Passkey-only; session cookie |
| Overview | `GET /api/v1/overview` | Fleet rollups and counts for the dashboard |
| Hosts | `GET/POST/PATCH /api/v1/hosts`, `GET /api/v1/hosts/{id}` | Hardware metadata and agent association |
| Health | `GET /api/v1/hosts/{id}/health`, `/health-history` | Current components, snapshots, metrics |
| VMs | `GET /api/v1/hosts/{id}/vms` | Latest Hyper-V facts |
| Logon stats | `GET /api/v1/logon-stats` | Per-user/per-day security-logon aggregates; bounded with `hasMore` |
| Events | `GET /api/v1/events`, `GET /api/v1/events/{id}` | FTS5-backed server-side search |
| Saved searches | CRUD under `/api/v1/saved-searches` | Single-admin configuration |
| Sources/tokens | `GET /api/v1/sources`, `POST /api/v1/registration-tokens`, revoke actions | Raw registration token returned once |
| Alerts | CRUD/list/action endpoints under `/api/v1/alerts` | Acknowledge and silence actions |
| Rules | CRUD under `/api/v1/rules` | Health, event, heartbeat, and threshold rules |
| Notifications | CRUD/test under `/api/v1/notification-channels` | Secrets write-only and redacted |
| Maintenance | CRUD under `/api/v1/maintenance-windows` | Host-scoped suppression windows |
| Settings | `GET/PATCH /api/v1/settings/retention` | Retention policy and safe operational settings |
| Passkeys | list/register/remove under `/api/v1/auth/passkeys` | Cannot remove the final usable passkey |
| Audit | `GET /api/v1/audit-log` | Filterable configuration/auth history |

Exact request and response schemas are generated into the OpenAPI document.
The following rules apply to all web resources:

- Every protected endpoint requires the authenticated single-admin session.
- Every configuration mutation writes an `audit_log` record in the same logical
  operation as the change.
- Secret-bearing create/update requests accept secret fields, but read
  responses return only metadata such as label, kind, created time, and rotated
  time.
- Destructive operations require an explicit action endpoint or confirmation
  field rather than being hidden behind a normal `PATCH`.
- Hosts without an agent remain valid hardware records; sources and hosts are
  not implicitly interchangeable.

### 7.1 Overview and host details

`GET /api/v1/overview` is an aggregation endpoint optimized for the initial
page. It returns the host tiles, health rollups, agent-silence state, alert
counts, and the timestamp at which the data was generated. It must not return
all events or all component history.

`GET /api/v1/hosts/{id}` returns the selected host, latest rollup, agent status,
component summaries, recent critical alerts, and a bounded recent-event preview.
Separate history endpoints return chart data at a requested time range and
server-selected resolution. The API is responsible for downsampling or
bucketing; the browser must not download an entire multi-year metric series.

Host create/update accepts `idracUrl` plus write-only `idracUsername`/
`idracPassword` — both required when setting credentials; on update,
omitted/blank leaves the stored credential unchanged. Read responses expose
only `idracCredentialSet`, never the credential value.

### 7.2 Event search

`GET /api/v1/events` accepts:

```text
from, to, hostId, sourceId, channel, severityMin, eventId,
q, limit, cursor, sort
```

The service translates these fields into parameterized SQLite queries. `q`
uses the FTS5 rendered-message index; structured filters use indexed columns.
The cursor encodes the last `(time, id)` position and is opaque to the client.
The API caps time ranges and page sizes to prevent an accidental full-database
scan. A detail request may return the rendered message, fields, and escaped raw
payload, but never treats event content as trusted HTML.

### 7.3 Rules, alerts, and actions

Rules are stored as typed records with a JSON match document validated according
to the rule type. The API validates the JSON shape before persistence and
returns a normalized representation to the frontend.

Alert actions are explicit:

- acknowledge an alert;
- clear an acknowledgement if permitted;
- create or end a silence; and
- create or remove a maintenance window.

Every action records the authenticated actor, target, previous state, new state,
and reason in `audit_log`.

### 7.4 Notification channels

A channel create/update request may contain a Telegram token/chat ID, webhook
URL, or SMTP fields. The API encrypts those fields immediately with the vault
key. The response includes a redacted configuration summary only. A `test`
endpoint queues or performs a clearly labeled test notification and records the
result without revealing the secret.

---

### 7.5 Logon stats

`GET /api/v1/logon-stats` returns per-user/per-day security-logon aggregates
derived server-side from accepted Security-channel events (PROTOCOL §6.6):
4624 interactive/RDP successes, 4625 failures (all types), 4740 lockouts.
Query parameters: `from`/`to` (inclusive UTC day range), `sourceId` (exact),
`user` (exact), `limit` (default 50, max 200). Results are ordered by day
descending; a `hasMore` flag reports additional rows — there is no cursor, so
paging means narrowing filters or raising `limit`. Rows carry `day`
(UTC `yyyy-MM-dd`), `sourceId`/`sourceName`, `user`, `logonType` (`2`, `10`,
or `null` for lockouts), `successCount`, and `failureCount`.

## 8. Authentication and session design

### 8.1 Passkey ceremonies

The API owns all WebAuthn ceremony state and validation. The browser receives
only the challenge/options and posts the browser credential response back.

The web endpoints are:

```text
GET  /api/v1/auth/session
POST /api/v1/auth/passkeys/login/options
POST /api/v1/auth/passkeys/login/verify
POST /api/v1/auth/passkeys/register/options
POST /api/v1/auth/passkeys/register/verify
POST /api/v1/auth/logout
GET  /api/v1/auth/passkeys
POST /api/v1/auth/passkeys/register/options   (authenticated additional key)
DELETE /api/v1/auth/passkeys/{id}
```

The registration options endpoint is allowed unauthenticated only when the
`passkeys` table is empty and the request originates from the configured
localhost/trusted network. The API, not the static frontend route, enforces
this condition. Once a passkey exists, initial registration is closed and new
keys require an authenticated session.

The server stores the explicit RP ID and expected origin in configuration. It
uses the .NET FIDO2 library to validate challenge, origin, RP ID, user
verification, credential ID, signature counter, and allowed ceremony state.
Challenges are short-lived, single-use, and bound to the intended operation.

### 8.2 Session and CSRF

On successful verification, the API issues a persistent session cookie with:

```text
HttpOnly; Secure; SameSite=Strict; Path=/
```

The cookie has a 14-day sliding expiry. It contains an opaque session ID whose
server-side record is revocable; it does not contain credentials or authorization
state that cannot be invalidated.

For unsafe web requests, the API also requires:

- an allowed `Origin` (and, where present, `Referer`) matching the configured
  frontend origin; and
- an anti-CSRF token supplied in a header and cookie pair.

The preferred same-origin deployment makes the origin policy simple. Separate
subdomains require an exact allow-list and credentialed CORS; wildcard origins
are forbidden.

### 8.3 Console reset

`hyveman-api auth reset`, `auth list-passkeys`, and `auth remove-passkey` are
local administrative commands. Reset clears passkeys and relevant ceremony
state, writes an audit/startup record where possible, and causes the trusted
network setup flow to become available again. There is no remote recovery API.

---

## 9. Background services

The API registers these hosted services with bounded cancellation and graceful
shutdown:

### 9.1 Hardware poller

`HardwarePollingService` schedules registered hosts independently. The default
interval is 60 seconds. Each poll:

1. loads the host's iDRAC URL and credential reference;
2. decrypts credentials only for the duration needed by the provider;
3. calls the configured Redfish resources using a bounded `HttpClient`;
4. maps overall and component status to the vendor-neutral model;
5. writes current components, a health snapshot, and metrics; and
6. submits health-state changes to the alert evaluator.

A failed host poll records the failure and last-success time without erasing the
last known component state. Timeouts and repeated failures use backoff so one
unreachable iDRAC cannot consume all worker capacity.

`IHardwareProvider` is the boundary for future generic Redfish, HPE iLO, or
SNMP providers. The API layer never depends on Dell-specific JSON types.

### 9.2 Heartbeat monitor

`HeartbeatMonitorService` evaluates the receive-time age of each source's last
heartbeat. It creates, updates, and clears agent-silent alerts according to the
configured rule and maintenance windows. It must run independently of telemetry
requests so silence alerts still fire when no new request arrives.

### 9.3 Alert evaluator

The evaluator handles:

- health-state transitions;
- event matches by source/host, channel, event ID, severity, and message
  expression;
- heartbeat silence;
- threshold crossings; and
- deduplication, cooldown, escalation, acknowledgement, and maintenance
  suppression.

A periodic reconciliation pass re-evaluates current heartbeat and hardware
state after restart. Event rules are evaluated as events are accepted; a later
reconciliation can repair state after a crash.

An alert has a stable fingerprint. The recommended uniqueness model is
`(rule_id, host_id, fingerprint, active state)`, allowing a resolved occurrence
to be followed by a new occurrence without losing history.

### 9.4 Notification dispatcher

Notifications use an outbox:

```text
alert state change -> notification_outbox row -> dispatcher -> provider
```

The outbox is durable, retryable, and records attempts, provider response class,
next attempt time, and final failure. It prevents a process crash between alert
commit and Telegram/webhook delivery from losing the notification. Secrets are
loaded through `ICredentialVault` and are never written to logs.

### 9.5 Retention and backup

`MaintenanceService` performs:

- event and metric retention purges;
- FTS maintenance and incremental vacuum where appropriate;
- daily `VACUUM INTO` snapshots;
- the 7 daily / 4 weekly / 12 monthly snapshot ladder; and
- cleanup of expired sessions and WebAuthn ceremony challenges.

The live SQLite database, server key, configuration, and snapshot output remain
under the single data directory specified by `DESIGN.md`. Snapshots retain the
existing encrypted credential blobs; they are not independently re-encrypted
in the MVP.

---

## 10. Persistence design

SQLite runs in WAL mode with foreign keys enabled, a bounded busy timeout, and
explicit migrations. The storage layer exposes repositories/services rather
than leaking `SqliteConnection` through the application layer.

The core tables are those in `DESIGN.md`, including `sources`, `hosts`,
`tokens`, `events`, `components`, `health_snapshots`, `metrics`, `vms`,
`alerts`, `rules`, `notification_channels`, `rule_channels`, `passkeys`,
`credentials`, `maintenance_windows`, and `audit_log`.

The implementation should add or materialize the following operational state:

```text
agent_status(
  source_id PRIMARY KEY,
  last_received,             -- server receive time for silence detection
  last_sent_at,               -- agent heartbeat sent_at, for diagnostics only
  agent_version,
  os_build,
  boot_time,                  -- boot session of the accepted heartbeat,
  uptime_s,
  degraded,
  config_hash,
  counters_json,
  heartbeat_json,
  updated_at
)

notification_outbox(
  id PRIMARY KEY,
  alert_id,
  channel_id,
  status,
  attempt_count,
  next_attempt_at,
  last_error,
  created_at,
  sent_at
)

web_sessions(
  id_hash PRIMARY KEY,
  created_at,
  expires_at,
  last_seen,
  revoked_at
)

webauthn_challenges(
  challenge_hash PRIMARY KEY,
  operation,
  created_at,
  expires_at,
  origin_context
)
```

Exact columns may be adjusted during migrations, but the following invariants
are required:

- `UNIQUE(kind, name)` on `sources`, which implements the v1 authoritative
  `(kind, hostname)` registration lookup;
- `UNIQUE(source_id, dedup_scope, record_id)` on `events`;
- foreign keys are enforced;
- credentials and session identifiers are never stored plaintext where a hash
  or ciphertext is sufficient;
- all stored times are UTC; and
- event message FTS rows are updated atomically with accepted event rows.

The `ILogStore`/`IMetricStore` abstraction remains the replacement seam if
SQLite is later replaced by ClickHouse or another store.

### 10.1 Credential vault

At startup, the API loads key `K` from the protected data directory. Credential
values are encrypted with AES-GCM using a fresh nonce per value. The database
stores ciphertext, nonce, authentication tag, key version, and metadata needed
for rotation. The key file and data files are ACLed to the service account and
local administrators.

A vault failure prevents hardware polling or notification delivery but should
not make log ingestion silently discard events. The readiness endpoint reports
the dependency state and the alert/diagnostic log reports the failure.

---

## 11. Configuration and deployment

Configuration is loaded in this order:

1. safe built-in defaults;
2. configuration file in the data directory;
3. environment variables/command-line overrides; and
4. explicitly supplied deployment settings.

The configuration must include at least:

```text
DataDirectory
PublicOrigin
ApiListenUrls
WebAuthnRpId
WebAuthnExpectedOrigin
ServerVersion
AgentProtocolCurrentVersion
AgentProtocolSupportedVersions
SQLiteBusyTimeoutMs
LogRetention
HardwarePollInterval
HeartbeatSilenceThreshold
RateLimits
VaultKeyPath
```

No token, password, or credential value should be required in the normal
configuration file. Those values enter through registration/admin API requests
and are persisted only as hashes or vault ciphertext.

The API publishes OpenAPI in development and can publish a versioned, access-
controlled document in production. OpenAPI is generated from the web API DTOs
and is the input to the frontend client generation step.

Startup sequence:

1. resolve data directory and configuration;
2. load/validate the vault key;
3. open SQLite and apply migrations;
4. verify protocol and WebAuthn configuration;
5. start the HTTP listener;
6. expose readiness only after required dependencies are available; and
7. start background services with cancellation support.

Shutdown stops accepting new writes, allows in-flight requests to finish within
a bounded grace period, cancels pollers/dispatchers, and closes SQLite cleanly.

---

## 12. Security and privacy

- HTTPS is mandatory for all public traffic. TLS termination must preserve the
  original scheme and host for origin/WebAuthn validation.
- Agent bearer tokens are accepted only on the exact protocol endpoints and are
  never accepted by web/admin routes.
- Registration tokens are single-use, short-lived where configured, scoped to a
  source kind, and never logged.
- Request bodies, event raw XML, authorization headers, and credential values
  are excluded or redacted from application logs.
- Admin UI input is treated as untrusted. Event messages and raw XML are
  returned as text, never rendered as HTML.
- iDRAC URLs and notification targets are admin-controlled and validated for
  supported schemes. The Redfish client must not follow arbitrary redirects.
- WebAuthn state, CSRF checks, rate limits, secure headers, and session
  revocation are enforced by the API rather than by frontend code.
- Add a restrictive CSP, `X-Content-Type-Options: nosniff`, frame protection,
  and appropriate referrer policy at the reverse proxy/API boundary.
- Audit authentication ceremonies, configuration changes, token creation and
  revocation, secret rotation, alert actions, and console resets.

---

## 13. Observability and failure behavior

Use structured JSON logs with:

- timestamp, level, event name, trace ID;
- source/host ID where relevant;
- endpoint and status code;
- duration and item counts; and
- a sanitized error category.

Never include raw bearer tokens, cookies, vault plaintext, or complete webhook
URLs.

Important operational counters include:

- accepted, deduped, and rejected log items;
- protocol/auth/version failures;
- telemetry heartbeats received and stale sources;
- Redfish poll success/failure/latency;
- active alerts and notification delivery attempts;
- SQLite busy/lock and migration failures; and
- backup completion and snapshot age.

The API must fail closed for authentication and fail visibly for storage or
vault failures. A transient notification provider failure must not roll back an
already committed event or alert. A failed background job is retried according
to its job policy and surfaced through logs/alerts.

---

## 14. Testing strategy

### Unit tests

- protocol validators and response builders;
- token parsing/hash lookup and scope decisions;
- event mapping, epoch IDs, severity handling, and alert fingerprints;
- rule matching, cooldowns, silences, and maintenance windows;
- Redfish normalization and credential-vault encryption; and
- web DTO validation and authorization policies.

### Integration tests

- real SQLite database in a temporary data directory;
- migrations, WAL behavior, FTS5 search, retention, and backup snapshots;
- idempotent log ingest with concurrent duplicate requests;
- latest-wins telemetry and heartbeat-silence transitions;
- notification outbox retries; and
- WebAuthn ceremony/session behavior using test credentials.

### HTTP contract tests

Run the `PROTOCOL.md` fixture suite against an in-memory or test-hosted API.
Verify headers, body `v`, status/error codes, response `commands`, gzip, limits,
and retry classification.

### Operational tests

- restore a snapshot on a clean host;
- restart during a log batch and verify retry/deduplication;
- stop an agent and verify the silence alert;
- revoke a token and verify the agent receives the specified auth behavior;
- lose an iDRAC and verify the last-known state plus poll failure are visible;
- rotate a credential and verify no plaintext appears in logs or responses.

---

## 15. Implementation order

1. Create the modular .NET host, configuration, logging, SQLite migrations, and
   readiness checks.
2. Implement protocol middleware, token storage, registration, and `/health`.
3. Implement idempotent log ingest, FTS5, retention, and protocol contract tests.
4. Implement latest-wins telemetry, `agent_status`, VM state, and heartbeat
   monitoring.
5. Implement the web session/passkey API and OpenAPI generation.
6. Implement overview, hosts, events, and saved-search web endpoints.
7. Add Redfish polling, health snapshots, metrics, and normalized components.
8. Add rules, alerts, acknowledgement/silence, maintenance windows, and audit.
9. Add Telegram/webhook outbox delivery and channel administration.
10. Add `VACUUM INTO` backups, restore verification, and production hardening.

The agent wire contract must remain compatible with `PROTOCOL.md` throughout
this sequence. Changes to the web API can be additive within `/api/v1`; a
breaking agent protocol change requires a protocol version decision first.
