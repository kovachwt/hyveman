# hyveman-api

The Hyveman backend: agent ingest endpoints (`PROTOCOL.md`), the web/admin API
(`API.md`, consumed by `hyveman-web`), the alert engine, Redfish hardware
polling, notifications, and SQLite storage. .NET 10 / ASP.NET Core.

## Build & run

```bash
dotnet build Hyveman.Api.sln
dotnet run --project src/Hyveman.Api -- --data-dir /path/to/data
```

First start creates the data directory: `hyveman.db` (SQLite, WAL), `vault.key`
(AES-GCM server key — back it up with the data directory), `config.json`,
`logs/`, and `backup/`.

Default listener: `http://127.0.0.1:5080`. HTTPS is mandatory for the agent
protocol in production (`AllowInsecureHttp` exists for development only; the
normal topology terminates TLS at a reverse proxy — IIS/nginx/Caddy — which
forwards `/register`, `/ingest/*`, `/health` and `/api/` to this process).

## Configuration

Loaded from: built-in defaults → `{data-dir}/config.json` → `HYVEMAN_*`
environment variables → command line (`--Key=value`). The full surface is
`HyvemanOptions` in `src/Hyveman.Api/Options.cs`; the important keys:

| Key | Default | Meaning |
|---|---|---|
| `DataDirectory` | `data` | All state lives here (DESIGN §9) |
| `ApiListenUrls` | `http://127.0.0.1:5080` | `;`-separated listener set |
| `PublicOrigin` / `WebAuthnExpectedOrigin` | `http://localhost:5080` | Browser origin for CSRF/WebAuthn; set to the frontend origin in production |
| `WebAuthnRpId` | `localhost` | Explicit RP ID (restore invariant, API.md §8.1) |
| `SQLiteBusyTimeoutMs` | 5000 | Busy timeout |
| `HardwarePollIntervalS` | 60 | iDRAC poll interval |
| `HeartbeatSilenceThresholdS` | 300 | Default agent-silent rule threshold |
| `RateLimits__GlobalPerMinute` etc. | 1200 / 300 / 20 / 30 | Protocol rate budgets |
| `VaultKeyPath` | `{data}/vault.key` | AES-GCM key file |
| `TrustedSetupNetworks` | loopback | CIDRs allowed to run first-run setup |
| `AllowedOrigins` | — | Extra exact origins for CSRF |

No token, password, or credential belongs in this file; secrets enter through
the admin API and are stored hashed or as vault ciphertext.

## Endpoints

- Agent protocol (PROTOCOL.md v1, own pipeline): `POST /register`,
  `POST /ingest/logs`, `POST /ingest/telemetry`, `GET /health` —
  `X-Hyveman-Protocol` required, gzip accepted, 4 MiB decompressed cap,
  bearer `reg_`/`agt_` tokens, per-item rejection semantics, `commands: []`
  reserved on every envelope.
- Web API (RFC 9457 Problem Details + stable `code`; session cookie +
  CSRF header): `/api/v1/*` — overview, hosts, health history, VMs, event
  search (FTS5 + cursor), saved searches, sources/registration tokens,
  alerts (acknowledge/silence), rules, notification channels, maintenance
  windows, retention settings, audit log, passkeys. OpenAPI document at
  `/openapi/v1.json` (development).
- Operations: `/health/live`, `/health/ready`.

## First-run setup

With an empty `passkeys` table, `POST /api/v1/auth/passkeys/register/options`
is permitted only from the trusted network (loopback by default). After the
first passkey is registered, setup is closed and further keys require an
authenticated session. There is no remote recovery:

```bash
hyveman-api auth list-passkeys --data-dir /path/to/data
hyveman-api auth remove-passkey <id> --data-dir /path/to/data
hyveman-api auth reset --data-dir /path/to/data
```

## Background services

- Hardware poller (Dell iDRAC Redfish → vendor-neutral components/snapshots/
  metrics, per-host backoff, last-known state preserved on failure);
- heartbeat monitor (agent-silent alerts by server receive time);
- alert evaluator + periodic reconciliation (event/health/heartbeat/threshold
  rules, cooldown, maintenance suppression, dedup);
- notification dispatcher (durable outbox → Telegram/webhook/SMTP with
  retry/backoff);
- maintenance (retention purges, FTS optimize, session/challenge cleanup,
  daily `VACUUM INTO` snapshots with the 7 daily / 4 weekly / 12 monthly
  ladder).

## Tests

```bash
dotnet test Hyveman.Api.sln
```

- `Hyveman.Protocol.Tests` / `Hyveman.Contract.Tests` — schema + validator +
  golden wire fixtures against `docs/schemas/protocol-v1.json`;
- `Hyveman.Application.Tests` — registration, rule engine, latest-wins
  ordering, cursors;
- `Hyveman.Infrastructure.Tests` — SQLite migrations/FTS5/idempotency,
  vault round-trip, backup ladder, Redfish normalization;
- `Hyveman.Api.Tests` — HTTP contract suite over a test-hosted instance
  (versions, auth, limits, gzip, health, web session/CSRF).
