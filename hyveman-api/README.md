# hyveman-api

The Hyveman backend: agent ingest endpoints, the web/admin API, the alert
engine, Redfish hardware polling, notifications, and SQLite storage.
.NET 10 / ASP.NET Core, one deployable process (a modular monolith).

| Doc | Role |
|---|---|
| [`../docs/DESIGN.md`](../docs/DESIGN.md) | System design contract |
| [`../docs/PROTOCOL.md`](../docs/PROTOCOL.md) | Agent wire protocol spec (v1) — the network boundary with `hyveman-agent` |
| [`../docs/API.md`](../docs/API.md) | This backend's implementation design (authoritative for this repo) |
| [`../docs/FRONTEND.md`](../docs/FRONTEND.md) | Web frontend contract (`hyveman-web` consumes `/api/v1`) |
| [`../docs/AGENT.md`](../docs/AGENT.md) | Agent build contract (`hyveman-agent` implements it) |

`PROTOCOL.md` is authoritative for the agent wire contract; `API.md` is
authoritative for this codebase. The agent protocol and the web API are
deliberately separate contracts.

## What's here

- **Agent protocol** (`PROTOCOL.md` v1, own middleware pipeline):
  `POST /register`, `POST /ingest/logs`, `POST /ingest/telemetry`,
  `GET /health` — `X-Hyveman-Protocol` versioning, `reg_`/`agt_` bearer
  tokens (hash-only storage), idempotent log batches
  (`(source_id, dedup_scope, record_id)`), latest-wins telemetry,
  per-item rejection semantics, gzip, 4 MiB decompressed cap, rate limits
  with `Retry-After`, reserved `commands: []` on every envelope.
- **Web/admin API** (`/api/v1/*`): session-cookie + CSRF protected, RFC 9457
  Problem Details with stable `code`s, passkey-only authentication
  (WebAuthn), overview/hosts/events/alerts/rules/channels/maintenance/settings/
  audit — secrets are write-only, destructive actions need confirmation,
  every mutation writes an audit row. Security logons (4624/4625/4740) are
  aggregated server-side into per-user/per-day logon stats (DESIGN §4.1).
- **Background services**: Dell iDRAC Redfish poller (vendor-neutral health
  model), heartbeat monitor (agent-silent alerts), alert evaluator +
  reconciliation, notification outbox dispatcher (Telegram/webhook/SMTP),
  retention/backup maintenance (`VACUUM INTO`, 7/4/12 ladder).
- **Storage**: SQLite in WAL mode, foreign keys enforced, FTS5 event search,
  AES-GCM credential vault, explicit migrations — all state in one data
  directory.

## Repository layout

```
src/
  Hyveman.Api/                    ASP.NET Core host, routing, middleware,
                                  controllers, background services, options
  Hyveman.Application/            use cases, ports, orchestration
  Hyveman.Domain/                 entities, value objects, rule semantics
  Hyveman.Protocol/               agent DTOs, validators, envelope builder,
                                  embedded protocol-v1.json schema validator
  Hyveman.Contracts/              web API DTOs (OpenAPI-facing)
  Hyveman.Infrastructure.Sqlite/  repositories, migrations, FTS5
  Hyveman.Infrastructure.Redfish/ Dell iDRAC Redfish provider
  Hyveman.Infrastructure.Security/ token hashing, AES-GCM vault, passkeys
  Hyveman.Infrastructure.Notify/  Telegram, webhook, SMTP, outbox sender

tests/
  Hyveman.Protocol.Tests/         schema + per-item/telemetry validation
  Hyveman.Application.Tests/      registration, rules, latest-wins, cursors
  Hyveman.Infrastructure.Tests/   SQLite migrations/FTS5/idempotency, vault,
                                  backups, Redfish normalization
  Hyveman.Api.Tests/              HTTP contract suite (test-hosted instance)
  Hyveman.Contract.Tests/         golden wire fixtures vs protocol-v1.json
```

## Build & run

Requires the .NET 10 SDK.

```bash
dotnet build Hyveman.Api.sln
dotnet run --project src/Hyveman.Api -- --data-dir /path/to/data
```

First start creates the data directory: `hyveman.db` (SQLite, WAL),
`vault.key` (AES-GCM server key — back it up together with the data
directory), `config.json`, `logs/`, and `backup/`.

Default listener: `http://127.0.0.1:5080`. **HTTPS is mandatory for the agent
protocol in production** (`AllowInsecureHttp` is a development/test-only
escape hatch). The normal topology terminates TLS at a reverse proxy —
IIS/nginx/Caddy — that forwards the agent paths and `/api/` to this process
(see Deployment below).

### Windows service

The host is wired with `UseWindowsService` (`hyveman-api` service name), so
on Windows Server it can run under the Service Control Manager; on Linux,
Docker, or in a terminal the same binary runs as a console process. Publish
and install:

```powershell
# from the repository root
dotnet publish src/Hyveman.Api -c Release -r win-x64 --self-contained -o publish
New-Service -Name hyveman-api -BinaryPathName "C:\hyveman\publish\hyveman-api.exe --data-dir C:\hyveman\data" `
  -StartupType Automatic -Description "Hyveman backend"
Start-Service hyveman-api
```

(`sc.exe create hyveman-api binPath= ...` works identically.) The service
account needs read/write on the data directory; snapshots and the vault key
stay inside it per the single-data-directory rule (DESIGN §9).

### First-run setup

With an empty `passkeys` table, passkey registration is permitted only from
the trusted network (loopback by default, `TrustedSetupNetworks` to widen).
After the first passkey, setup is closed; additional keys require an
authenticated session. There is deliberately **no remote recovery**:

```bash
hyveman-api auth list-passkeys --data-dir /path/to/data
hyveman-api auth remove-passkey <id> --data-dir /path/to/data
hyveman-api auth reset --data-dir /path/to/data
```

## Configuration

Loaded in this order: built-in defaults → `{data-dir}/config.json` →
`HYVEMAN_*` environment variables → command line (`--Key=value`). The full
surface is `HyvemanOptions` in `src/Hyveman.Api/Options.cs`; the important
keys:

| Key | Default | Meaning |
|---|---|---|
| `DataDirectory` | `data` | All state lives here |
| `ApiListenUrls` | `http://127.0.0.1:5080` | `;`-separated listener set |
| `PublicOrigin` / `WebAuthnExpectedOrigin` | `http://localhost:5080` | Browser origin for CSRF/WebAuthn; set to the frontend origin in production |
| `WebAuthnRpId` | `localhost` | Explicit RP ID (API.md §8.1) |
| `SQLiteBusyTimeoutMs` | 5000 | Busy timeout |
| `LogRetentionDays` | 365 | Default event retention |
| `HardwarePollIntervalS` | 60 | iDRAC poll interval |
| `HeartbeatSilenceThresholdS` | 300 | Seeded agent-silent rule threshold |
| `RateLimits__GlobalPerMinute` / `PerSourcePerMinute` / `RegistrationPerMinute` / `AuthPerMinute` | 1200 / 300 / 20 / 30 | Protocol rate budgets |
| `VaultKeyPath` | `{data}/vault.key` | AES-GCM key file |
| `TrustedSetupNetworks` | loopback | CIDRs allowed to run first-run setup |
| `AllowedOrigins` | — | Extra exact origins for CSRF/Origin checks |

No token, password, or credential belongs in the config file; secrets enter
through the admin API and are persisted only as hashes or vault ciphertext.

## Endpoints

- **Agent protocol** (PROTOCOL.md v1, own pipeline): `POST /register`,
  `POST /ingest/logs`, `POST /ingest/telemetry`, `GET /health` — version
  header checked before body/auth; error envelopes carry the server's
  current `v`, `error.supported` on version errors, and `commands: []`;
  per-item rejection reasons (`raw_oversize`, `bad_time`, `schema`, …);
  `400 too_many_items` / `413 payload_too_large` trigger agent-side batch
  splitting; `Retry-After` on 429/503.
- **Web API** (`/api/v1/*`): overview, hosts + health/history + VMs, event
  search (FTS5, cursor-paginated, max 200), saved searches,
  sources/registration tokens/revoke, alerts (acknowledge/silence/unsilence),
  rules, notification channels (+ test), maintenance windows, retention
  settings, audit log, logon stats (per-user/per-day security-logon
  aggregates), passkeys, session. OpenAPI document at
  `/openapi/v1.json` (development).
- **Operations**: `/health/live`, `/health/ready`. (`GET /health` is the
  agent-protocol endpoint and must not be confused with these.)

## Deployment

Preferred public topology — one origin, TLS terminated by the proxy:

```text
https://hyveman.example.com/          -> hyveman-web static files
https://hyveman.example.com/api/...   -> this process (web API)
https://hyveman.example.com/register  -> this process (agent protocol)
```

The agent endpoint paths are fixed by PROTOCOL.md and must not be renamed or
moved under `/api`. Preserve the original scheme and host through the proxy
(`X-Forwarded-Proto`/`X-Forwarded-For`; loopback proxies trusted by default,
add remote proxies to `KnownProxies`). Run as a Windows service, a systemd
unit, or a container; HTTPS, secure headers (CSP, `nosniff`, frame
protection, referrer policy), and the frontend routing are the proxy's job.

## Background services

- **Hardware poller** — per-host schedule with exponential backoff on
  failure; a failed poll records the failure without erasing the last known
  component state.
- **Heartbeat monitor** — evaluates server receive-time age of each source's
  heartbeat against heartbeat rules and maintenance windows; runs
  independently of telemetry requests.
- **Alert evaluator + reconciliation** — health transitions, event matches,
  heartbeat silence, threshold crossings; cooldown, dedup, maintenance
  suppression; periodic reconciliation repairs state after a crash.
- **Notification dispatcher** — durable outbox (`alert state change →
  outbox row → dispatcher → provider`), retryable, never rolls back an
  already-committed event/alert.
- **Maintenance** — retention purges (events/metrics/snapshots), FTS
  optimize, expired session/challenge/window cleanup, daily `VACUUM INTO`
  snapshots with the 7 daily / 4 weekly / 12 monthly ladder.

## Observability

Structured JSON logs (Serilog) to console and `{data}/logs/hyveman-api-.log`
(14 files kept). Request logging includes trace IDs; bearer tokens, cookies,
vault plaintext, raw payloads, and webhook URLs are excluded from logs.
Operational counters cover accepted/deduped/rejected log items, protocol/auth
failures, telemetry heartbeats, Redfish poll results, alert and notification
activity, and SQLite/backup health — the alert/audit layer surfaces what the
logs don't.

## Tests

```bash
dotnet test Hyveman.Api.sln
```

- `Hyveman.Protocol.Tests` — schema + endpoint validators: per-item
  rejection reasons, severity-per-kind, size caps, epoch record IDs,
  forward-compatible unknown fields.
- `Hyveman.Contract.Tests` — golden wire fixtures: every response shape the
  server can produce validates against `docs/schemas/protocol-v1.json`
  (embedded at build time), all stable error codes, version-error envelope
  invariants, UTC `Z` timestamps.
- `Hyveman.Application.Tests` — registration semantics, rule matching,
  latest-wins ordering, cursors, conflicts, security-logon aggregation
  (4624 LogonType 2/10 curation, 4625/4740 failures, string/number
  `LogonType` forms, same-batch merging).
- `Hyveman.Infrastructure.Tests` — real SQLite: migrations, WAL, FTS5,
  idempotent duplicate ingestion, vault round-trip, backup ladder,
  logon_stats upsert (incl. NULL-logontype lockout merging) and filters.
- `Hyveman.Api.Tests` — HTTP contract suite over a test-hosted instance:
  version missing/unsupported/mismatched, token lifecycle (invalid/revoked/
  wrong-scope/consumed), mixed batches + invariant, gzip, body/limit errors,
  telemetry latest-wins, health with/without token, rate-limit `Retry-After`,
  web session/CSRF/Problem Details, edge cases (non-integer body `v`,
  missing `Content-Type`), end-to-end logon-stats aggregation with
  deduped-replay immunity.

The protocol suite is a compatibility test, not merely controller unit
testing: `PROTOCOL.md` wins over any stricter schema detail, and a wire
change requires a protocol version decision before implementation (API.md
§6.8).
