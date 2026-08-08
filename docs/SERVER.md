# Hyveman Server — Technical Design (v1)

This is the **build contract for `hyveman-server`**: the always-running backend
that hosts the ingest API, hardware health poller, alert engine, storage,
notification dispatch, and the Blazor web UI (DESIGN §3, §4.2–§4.5). It takes
the system contract (`docs/DESIGN.md`) and the wire contract
(`docs/PROTOCOL.md`) and specifies how the server is structured internally to
implement them.

- **Companion docs:** `docs/DESIGN.md` (system contract — authoritative for
  goals, architecture, data model, and the decisions log §13), `docs/PROTOCOL.md`
  (wire contract — authoritative for transport, endpoints, auth, versioning,
  the idempotency key, error semantics). Where this doc and PROTOCOL.md
  describe the same wire behavior, **PROTOCOL.md is the spec**; this doc
  describes the server-side *implementation* of that spec.
- **Status:** v1 — covers Phase 1 MVP and the structural decisions that make
  Phase 2/3 (agent-side Hyper-V collection and VM UI, full alert engine,
  syslog, command channel) additive.
- **Protocol version served:** `1` (PROTOCOL §3). `error.supported: [1]`.

---

## 1. Scope & responsibilities

### 1.1 In scope (server owns)
1. **Ingest API** — `POST /register`, `POST /ingest/logs`,
   `POST /ingest/telemetry`, `GET /health` over HTTPS (PROTOCOL §5–§8).
2. **Hardware poller** — iDRAC Redfish per registered Dell host, normalized to
   a vendor-neutral component health model (DESIGN §4.2).
3. **Alert engine** — health, event, heartbeat, and threshold rule model with
   dedup, cooldown, escalation, maintenance windows, ack/silence (DESIGN
   §4.4); Phase 1 evaluates health and heartbeat rules, while event and
   threshold evaluation arrives in Phase 2.
4. **Notification dispatcher** — `INotifier` providers (Telegram, webhook,
   optional SMTP) with per-rule fan-out (DESIGN §4.4).
5. **Storage** — SQLite (WAL + FTS5), schema, migrations, retention purge,
   incremental vacuum (DESIGN §4.3, §13 #10).
6. **Web UI (Blazor Server)** — fleet overview, per-host view, log search,
   alerting UI, admin (host registration, iDRAC vault, channels, retention,
   passkey management) (DESIGN §4.5).
7. **Auth** — agent token lifecycle + passkey-only web login + credentials
   vault (DESIGN §7, §8, PROTOCOL §4).
8. **Backup** — daily `VACUUM INTO` hot snapshots + retention ladder
   (DESIGN §9).
9. **Self-observability** — structured logging, health checks, own-metrics.

### 1.2 Out of scope (separate specs / later phases)
- **Agent** — `docs/AGENT.md` (the agent build contract). The server is
  agnostic to agent internals; only the wire contract (PROTOCOL.md) matters.
- **Syslog receiver transport** — Phase 3 (DESIGN §11 #3). The envelope and
  `events` schema already accommodate syslog-originated events; only the
  *transport* (UDP/TCP 514, optional TLS 6514, RFC 3164/5424) is unbuilt.
- **Command channel** — reserved in the protocol (PROTOCOL §16, DESIGN §12);
  the server always emits `"commands": []` and never enqueues commands in v1.
- **ClickHouse / VictoriaLogs store swap** — `ILogStore`/`IMetricStore`
  generalization is deferred (DESIGN §13 #10); MVP uses SQLite directly behind
  a thin repository layer (§6.4) so the swap is a later, contained change.

### 1.3 Non-goals
- Multi-tenant operation, multi-admin RBAC (single-admin passkey model, DESIGN
  §8), horizontal scaling, or a separate web/frontend build pipeline (Blazor
  Server keeps one toolchain, DESIGN §6, §13 #7).

---

## 2. Technology stack

| Piece | Choice | Rationale |
|---|---|---|
| Runtime / language | **.NET 8 (LTS)**, C# | DESIGN §6; native HTTP/Kestrel, Windows-service hosting, `System.Text.Json`, good async story; targets the DESIGN minimum of Windows Server 2019 and is **cross-platform by design** (the same binary runs on Linux via systemd/Docker — Windows-specific code is confined to `OperatingSystem.IsWindows()`-guarded ACL helpers and service registration); LTS through Nov 2026. |
| Web host | **ASP.NET Core / Kestrel** | One host serves both the ingest API and Blazor Server on the same TLS listener (§4). |
| Web UI | **Blazor Server** | DESIGN §13 #7; single toolchain, no separate JS build/deploy, ideal for internal tool. |
| Ingest endpoints | **Minimal API** (`MapPost`/`MapGet`) | Hot path, low allocation, easy to bind to the wire envelope with `System.Text.Json` source generation. |
| Admin/web UI data | Blazor components + thin services | No SPA; server-rendered with SignalR circuit. |
| Storage | **SQLite + Microsoft.Data.Sqlite**, WAL mode, FTS5 | DESIGN §13 #1/#10; zero-ops at this fleet size. |
| Data access | **Dapper** + hand-written SQL | FTS5 virtual tables, `ON CONFLICT … DO NOTHING`, `VACUUM INTO`, `PRAGMA incremental_vacuum`, and WAL pragmas are first-class in raw SQL; Dapper keeps mapping cheap and allocation low on the ingest hot path. EF Core is *acceptable* for admin tables but rejected for the hot path (adds overhead + abstraction over SQLite-specific SQL). One data-access style across the codebase. |
| JSON | **`System.Text.Json`** + source generators | Deterministic, AOT-friendly, fast envelope (de)serialization for ingest. |
| Hardware (Redfish) | `HttpClient` + `System.Text.Json` | DESIGN §4.2. |
| Hardware (SNMP, optional Phase 2) | **Lextm.SharpSnmpLib** | DESIGN §6; trap receiver, deferred. |
| Passkeys | **Fido2 library** (passwordless.dev) | DESIGN §8; WebAuthn/FIDO2 server-side. Small JS interop in Blazor for the ceremony. |
| Logging | **Serilog** → rolling file in data dir + console | Structured, low-friction; one sink family. |
| Secrets | AES-GCM via `System.Security.Cryptography` | DESIGN §7; no external KMS; key K in data dir. |
| Packaging | **Self-contained single-file exe** + `install.ps1` | DESIGN §6, §13 #2; manual install now, GPO/MSI-deployable later. |

---

## 3. Process model & runtime

### 3.1 Single process, one host
`hyveman-server` is **one .NET process** hosting one ASP.NET Core
`WebApplication`. The same Kestrel listener serves:
- the agent-facing ingest API (`/register`, `/ingest/*`, `/health`), and
- the browser-facing Blazor UI (`/`, `/dashboard`, `/search`, `/alerts`, `/admin`, `/auth/*`).

Both share TLS, the DI container, the storage layer, and the background
services. Splitting ingest and UI onto separate ports/bindings is supported by
config (§5.3) but not required for MVP — one listener behind TLS is simplest.
Agent and iDRAC traffic is expected to remain on the internal fleet network,
but the UI may be internet-exposed (DESIGN §8); TLS, passkeys, rate limiting,
and firewall/reverse-proxy policy remain mandatory.

### 3.2 Hosted services (background workers)
Long-running work is implemented as `IHostedService`/`BackgroundService`
registered in DI, started/stopped by the generic host with graceful shutdown:

| Service | Trigger | Responsibility | Phase |
|---|---|---|---|
| `HardwarePollerService` | interval (default 60s/host) | Redfish poll → normalize → write `components`/`metrics`/`health_snapshots` | 1 |
| `AlertEngineService` | event-driven + 10s sweep | Evaluate rules, dedup/update alerts, enqueue notifications | 1 (health + heartbeat), 2 (event/threshold) |
| `AgentSilenceWatchdog` | 30s sweep | Detect missed heartbeats vs per-source threshold → heartbeat alert | 1 |
| `RetentionService` | daily | `DELETE FROM events WHERE time < ?`; `PRAGMA incremental_vacuum` | 1 |
| `BackupService` | daily (configurable time) | `VACUUM INTO` snapshot + retention ladder | 1 |
| `RateLimitReaper` | periodic | Evict stale per-source rate-limit buckets | 1 |
| (Phase 2) `SnmpTrapService` | listener | Receive iDRAC traps → normalize → alert | 2 |
| (Phase 3) `SyslogReceiverService` | listener | UDP/TCP 514 (+ optional TLS 6514) → envelope → `events` | 3 |

### 3.3 Lifetime, shutdown, and crash-safety
- **Windows service** via `Microsoft.Extensions.Hosting.WindowsServices`
  (`sc.exe`-registered, or `install.ps1`). Also runnable as a console app for
  development/restore drills (DESIGN §9).
- **Graceful shutdown:** `IHostApplicationLifetime` `ApplicationStopping`
  cancels hosted services; in-flight HTTP requests drain; the SQLite
  connection pool closes. Ingest writes are committed transactionally per
  batch (§7.6), so a mid-batch shutdown rolls back the incomplete batch and
  simply resumes on the agent's next retry — no partial state.
- **Single-instance guard:** a file lock on `<data_dir>/state/server.lock` (a named mutex is
  only in-process on Unix, so the guard is a file lock everywhere) plus SQLite's own busy
  handling prevents two instances pointing at the same data dir from corrupting it.
- **Startup order:** load config → load server key K (§12.3) → open DB → run
  migrations (§6.5) → register hosted services → start Kestrel. `GET /health`
  returns `503 unavailable` until migrations complete and the DB is writable
  (PROTOCOL §8.2, §13.3).

---

## 4. High-level architecture

```
 ┌──────────────────────────── hyveman-server (single process, .NET 8) ────────────────────────────┐
 │                                                                                                 │
 │  Kestrel (HTTPS) ── middleware pipeline (§7.1): exception │ version │ gzip │ auth │ rate-limit │ json │
 │   │                                                                                             │
 │   ├─ Ingest API (Minimal API)            ┌─────────────────────────────────────────────────┐   │
 │   │   POST /register  ─► RegistrationSvc │  Storage layer (§6)                              │   │
 │   │   POST /ingest/logs ─► LogIngestSvc  │   Dapper repos → SQLite (WAL + FTS5)             │   │
 │   │   POST /ingest/telemetry ► TelemetryS│     events │ components │ metrics │ vms           │   │
 │   │   GET  /health ─► HealthCheck        │     alerts │ rules │ channels │ logon_stats       │   │
 │   │                                      │     sources│ tokens │ hosts │ passkeys            │   │
 │   └─ Blazor Server UI (§11)              │     credentials(vault) │ maintenance │ audit      │   │
 │       /auth/*  passkey login + setup      └───────────────┬─────────────────────────────────┘   │
 │       /dashboard /host/<id> /search                         │                                    │
 │       /alerts /admin (reg tokens, iDRAC vault, channels)    │                                    │
 │                                                             │                                    │
 │  Background services (§3.2) ───────────────────────────────┼─► reads/writes                    │
 │   HardwarePoller ─► Redfish ─► components/metrics/snapshots │                                    │
 │   AlertEngine ◄── (state changes, new events, metrics) ────┤                                    │
 │   AgentSilenceWatchdog ◄── (last heartbeat per source) ────┤                                    │
 │   RetentionService │ BackupService │ RateLimitReaper        │                                    │
 │                                                             │                                    │
 │   NotificationDispatcher ◄── alert enqueue ──► INotifier providers                            │
 │       TelegramNotifier │ WebhookNotifier │ SmtpNotifier(Phase 3)                              │
 │                                                                                                 │
 │   Cross-cutting: ICredentialVault (AES-GCM, key K) │ ITokenService │ AuditLog │ Serilog        │
 └─────────────────────────────────────────────────────────────────────────────────────────────────┘
        ▲                                  ▲                                  ▲
        │ agents (PROTOCOL.md)             │ operator browser (passkey)       │ iDRAC (Redfish, https)
```

### 4.1 Module map
| Module | Namespace (proposed) | Key types | Source-of-truth |
|---|---|---|---|
| Ingest API | `Hyveman.Server.Ingest` | `IngestEndpoints`, `LogIngestService`, `TelemetryService`, `RegistrationService`, `HealthEndpoints` | PROTOCOL §5–§8 |
| Middleware | `Hyveman.Server.Ingest.Middleware` | `ProtocolVersionMiddleware`, `AuthMiddleware`, `RateLimitMiddleware`, `GzipMiddleware` | PROTOCOL §3,§4,§9,§15 |
| Storage | `Hyveman.Server.Storage` | `SqliteStore`, `DbMigrator`, repos (`EventRepository`, `ComponentRepository`, …) | DESIGN §5, this doc §6 |
| Hardware | `Hyveman.Server.Hardware` | `HardwarePollerService`, `IRedfishProvider`, `DellRedfishProvider`, `ComponentNormalizer` | DESIGN §4.2, this doc §8 |
| Alerts | `Hyveman.Server.Alerts` | `AlertEngineService`, `RuleEvaluator` (per type), `AlertDeduper`, `MaintenanceWindowFilter` | DESIGN §4.4, this doc §9 |
| Notifications | `Hyveman.Server.Notifications` | `NotificationDispatcher`, `INotifier`, `TelegramNotifier`, `WebhookNotifier` | DESIGN §4.4, this doc §10 |
| Auth | `Hyveman.Server.Auth` | `ITokenService`, `TokenHasher`, `PasskeyService`, `ICredentialVault`, `AesGcmCredentialVault` | DESIGN §7,§8, PROTOCOL §4, this doc §12 |
| Web UI | `Hyveman.Server.Web` | Blazor components, `PasskeyAuthMiddleware`, page services | DESIGN §4.5,§8, this doc §11 |
| Backup/Retention | `Hyveman.Server.Maintenance` | `BackupService`, `RetentionService` | DESIGN §9, this doc §13 |
| Config | `Hyveman.Server.Config` | `ServerOptions`, `DataDirectory` | DESIGN §9, this doc §5 |

---

## 5. Configuration & data directory

### 5.1 Single data directory (DESIGN §9, rule)
**All persistent state lives in one directory.** Backup = "copy this folder";
restore = "point a fresh install at it." No registry, no machine-scoped DPAPI
(DESIGN §7), no state scattered elsewhere.

```
<data_dir>/                         # default: %ProgramData%\Hyveman\server  (ACL: SYSTEM + Administrators)
  hyveman.db                        # SQLite database (WAL)
  hyveman.db-wal
  hyveman.db-shm
  config/
    server.json                     # ServerOptions (§5.3)
    key                             # AES-GCM server key K (generated first run; ACL SYSTEM+Admins)  §12.3
    rp_id.txt                       # WebAuthn RP ID, explicit (DESIGN §8, §13 #14)
  certs/                            # Let's Encrypt mode only (§5.5)
    account-key.pem                 # ACME account key (registered once, reused for renewals)
    cert.pfx                        # issued chain; PFX password derived from key K (§5.5)
  backup/
    daily/   hyveman-YYYYMMDD.db    # VACUUM INTO snapshots
    weekly/  hyveman-YYYYWWww.db
    monthly/ hyveman-YYYYMM.db
  logs/
    server-.json rolling            # Serilog rolling file
  state/                            # ephemeral runtime files (rate-limit buckets are in-memory; nothing load-bearing here in MVP)
```

The data dir path is resolved as: `--data-dir` CLI arg → `HYVEMAN_DATA_DIR`
env → `ServerOptions.DataDir` in `config/server.json` → default
`%ProgramData%\Hyveman\server`. First-run bootstraps missing subdirs and a
random `key` (mode 0600-equivalent ACL: `SYSTEM` + `Administrators` only).

### 5.2 Server key K (DESIGN §7)
- 256-bit random key, generated on first run if `config/key` is absent.
- Read once at startup into a `ReadOnlyMemory<byte>` held by
  `AesGcmCredentialVault`; never written to disk again, never logged, never
  sent to agents/UI.
- **Not machine-scoped DPAPI** (DESIGN §7) — deliberately, so a restored
  backup folder (DB + `key`) is readable on a different machine.
- Backup of K is the operator's responsibility: the *same* VM/file backup that
  covers the data folder sweeps up `key` (DESIGN §9). Losing K = losing all
  secrets (documented; mitigated by the file backup).

### 5.3 `config/server.json` — `ServerOptions`
```jsonc
{
  "urls": "https://0.0.0.0:443",          // Kestrel bind; separate ingest/ui URLs allowed via Kestrel config
  "tls": {
    "cert_path": "config/cert.pfx",        // static cert (own CA or certbot-style) — mutually
    "cert_password": "",                   //   exclusive with lets_encrypt below; or reference
    "min_tls": "1.2", "preferred_tls": "1.3",   //   a credentials entry by label (preferred)
    "lets_encrypt": {                       // ACME auto-provisioning (§5.5); omit for static certs
      "enabled": true,
      "domains": ["hyveman.example.com"],  // public DNS names; no wildcards (http-01)
      "email": "admin@example.com",        // ACME account contact
      "staging": false,                     // true = LE staging endpoint (rate-limit-safe testing)
      "renew_days": 30,                     // renew when < 30 days remain (1..89; LE certs are 90-day)
      "http_port": 80                       // http-01 challenge listener + http→https redirect
    }
  },
  "ingest": {
    "max_batch_bytes": 4194304,            // 4 MiB (PROTOCOL §12)
    "max_items": 1000,                     // PROTOCOL §12
    "max_raw_bytes": 16384,                // server hard cap (PROTOCOL §12)
    "max_message_bytes": 65536,
    "max_field_bytes": 65536,
    "max_record_id_len": 128,
    "per_source_rate": { "requests_per_min": 120, "bytes_per_min": 33554432 },
    "global_rate":  { "requests_per_min": 1200 }
  },
  "poller": { "interval_s": 60, "timeout_s": 15, "concurrency": 4 },
  "alerts": { "sweep_s": 10, "default_heartbeat_miss_s": 180 },
  "notifications": {
    "webhook": { "allow_private": false, "allowed_hosts": [] }
  },
  "retention": {
    "events_days": 365,
    "metrics_days": 365,
    "health_snapshots_days": 365,
    "audit_days": 730,
    "resolved_alerts_days": 730,
    "vacuum_after_purge": true
  },
  "backup": { "time_local": "03:00", "keep_daily": 7, "keep_weekly": 4, "keep_monthly": 12 },
  "web": { "session_days": 14 },           // DESIGN §8 sliding cookie
  "logging": { "level": "Information", "file_retain_days": 14 }
}
```
Options are validated at startup; invalid → fail fast with a clear message
(no silent misconfiguration, especially around TLS/`urls`).

### 5.4 Secrets in config
TLS cert password and any other secret referenced by `server.json` **must not
appear in plaintext**. The preferred pattern: store the secret in the
`credentials` vault (§12.3) under a stable `label` and reference it by label
(`"tls.cert_password_ref": "label:tls-cert-password"`). `ServerOptions`
resolution substitutes from the vault at startup. The bootstrap exception is
the very first run with a passphrase-protected cert, handled by the setup
wizard (§11.3).

### 5.5 Let's Encrypt (ACME v2) auto-provisioning

When `tls.lets_encrypt.enabled` is set, the server **owns its certificate lifecycle**
(no certbot, no cron, no external tooling):

- **Flow** (`Certificates/AcmeCertificateManager`): load-or-register the ACME account
  key (`certs/account-key.pem`) → `new-order` for the configured domains → stage the
  http-01 key authorizations → validate → finalize → persist the chain as
  `certs/cert.pfx` → swap it into Kestrel. Runs as a background service with
  exponential backoff (1 min → 1 h) on failure; a certificate problem never blocks
  startup.
- **Challenge transport** (`Certificates/AcmeHttpMiddleware`): a plain-HTTP Kestrel
  listener on `tls.lets_encrypt.http_port` serves only
  `/.well-known/acme-challenge/<token>` (200 + key authorization, else 404) and
  308-redirects every other request to the HTTPS port from `urls`. The challenge
  port must be reachable from the internet on the server's public IP (direct, or via a
  reverse proxy that forwards the challenge path).
- **Certificate serving** (`Certificates/AcmeCertStore`): Kestrel uses a
  `ServerCertificateSelector` per handshake, so renewals swap atomically with no
  listener restart. Until the first order lands (or if the stored PFX is corrupt), a
  short-lived self-signed bootstrap certificate is served — HTTPS works from first
  boot; the real certificate replaces it within minutes.
- **Key material & backup**: the ACME account key and the issued PFX live in
  `certs/` inside the single data directory (§5.1) — the normal data-dir backup
  covers both. The PFX password is **derived from server key K** (SHA-256 of
  `K ‖ "hyveman-acme-pfx"`), so restoring a backup restores the certificate, and
  losing K loses the ability to decrypt the PFX (a fresh order re-issues it
  automatically). No new secrets to manage.
- **Renewal**: Let's Encrypt certificates are valid 90 days; renewal triggers when
  fewer than `tls.lets_encrypt.renew_days` days remain (default 30), checked every
  12 h. `staging: true` points at the Let's Encrypt staging endpoint for
  rate-limit-safe testing.
- **Limits**: http-01 cannot validate wildcard or single-label/IDN names — domains
  are validated at startup (fail fast).

---

## 6. Storage layer & schema

### 6.1 SQLite configuration (applied per connection)
```sql
PRAGMA journal_mode=WAL;          -- DESIGN §4.3 (concurrent readers + one writer)
PRAGMA synchronous=NORMAL;        -- safe with WAL, fast
PRAGMA busy_timeout=5000;         -- tolerate brief lock contention
PRAGMA foreign_keys=ON;
PRAGMA temp_store=MEMORY;
PRAGMA mmap_size=268435456;       -- 256 MiB; fleet DB is small, this is plenty
```
- **One write connection** (serialized) owned by a `SqliteWriter` that all
  repos delegate to for writes; readers use the connection pool
  (`Microsoft.Data.Sqlite` pooling, read-only where possible). WAL lets the
  Blazor UI and backup read concurrently with ingest writes.
- **Connection string** is `Data Source=<data_dir>/hyveman.db` with
  `Mode=ReadWriteCreate`, `Pooling=true`, `Default Timeout=5`.

### 6.2 Tables (full DDL in Appendix A; summary here)
Implements DESIGN §5.1/§5.2 exactly, with these **server-side extensions**
(noted as decisions):

- **`tokens`** — extended with `consumed_at`, `expires_at`, `bound_kind` to
  support single-use `reg_` tokens (PROTOCOL §4.1, §5.2). `source_id` is
  `NULL` for `reg_` tokens (no agent yet); set for `agt_` tokens unless the
  source was later deleted, which produces an orphaned token resolved as
  `unknown_source`. `scopes` are stored as a JSON array column.
- **`events`** — `UNIQUE(source_id, dedup_scope, record_id)` per DESIGN §13
  #11; `dedup_scope TEXT NOT NULL DEFAULT ''`; `record_id TEXT`. Indexes:
  `idx_events_src_time (source_id, time)`, `idx_events_channel (channel)`,
  `idx_events_eventid (event_id)`, `idx_events_time (time)` (retention purge).
- **`events_fts`** — FTS5 **external-content** table over `events.message`
  with trigger-based synchronization (§6.3).
- **`sources`** — `UNIQUE(kind, name)` plus optional `boot_id` fingerprint;
  the reinstall path is a single lookup while differing fingerprints trigger
  the protocol's disambiguated-source path (PROTOCOL §5.2 step 2).
- **`reg_tokens`** — modeled as `tokens` rows with `scope='register'`; no
  separate table. Admin issuance inserts a token row with `source_id=NULL`,
  `bound_kind`, `expires_at`, `consumed_at=NULL`.
- **`hosts`** — `idrac_cred_ref` points into `credentials` (vault label/row
  id), never a plaintext password (DESIGN §13 #13).
- **`notification_channels.config_ref`** — points into `credentials`; no
  plaintext secrets in channel config (DESIGN §4.4, §13 #13).
- **`alerts`** — `status` ∈ `active|acked|silenced|resolved`; `signature`
  column (hash of the rule's match identity) for dedup grouping; `count` +
  `last_seen` for the "same alert, bumped" behavior (DESIGN §4.4).

### 6.3 FTS5 full-text index
```sql
CREATE VIRTUAL TABLE events_fts USING fts5(
  message,
  content='events', content_rowid='id',
  tokenize='unicode61 remove_diacritics 1'
);
-- triggers keep events_fts in sync with events (insert / delete-on-replace / retention delete)
```
- Search uses `events_fts MATCH ?` joined back to `events` on `rowid`. The UI
  search (§11) composes FTS with the structured filters (`time`, `source_id`,
  `channel`, `severity`, `event_id`) in the same query.
- **Retention interaction:** the purge job deletes `events` rows older than
  `retention.events_days`; FTS sync triggers remove the corresponding FTS
  entries. `PRAGMA incremental_vacuum` reclaims pages (DESIGN §13 #10).

### 6.4 Storage abstraction (DESIGN §13 #10 — deferred)
MVP talks to SQLite through **thin repository interfaces** scoped to the
domain (`IEventRepository`, `IComponentRepository`, …), each implemented by a
Dapper-backed `Sqlite*Repository`. The **`ILogStore`/`IMetricStore`**
generalization (the contract that would let ClickHouse/VictoriaLogs replace
SQLite *without touching ingest/alerts/UI*) is **deferred** per DESIGN
§13 #10 — not built in MVP, but the repository seams are placed so that
introducing `ILogStore` later is a contained refactor (repos move behind it,
callers unchanged). **Do not** pre-build the abstract store now — it would
speculate an API before a second backend exists to validate it.

### 6.5 Migrations
- **Simple, in-order, idempotent SQL migrations** versioned in a
  `schema_migrations(version INT PK, applied_at)` table. Each migration is a
  `.sql` resource applied inside a transaction; a bump in the embedded version
  number runs only newer files. No EF Core migrator (we don't use EF).
- **No destructive auto-migration:** additive changes only across releases;
  any column drop/rename ships as an explicit migration with backfill.
- Migrations run at startup (§3.3) before the server accepts traffic; while
  running, `GET /health` returns `503` (PROTOCOL §8.2, §13.3).

---

## 7. Ingest API — implementation of PROTOCOL v1

This section is the server-side implementation of PROTOCOL §5–§8. The wire
contract itself is **not** re-stated; only the implementation behavior is.

### 7.1 Middleware pipeline (order matters)
```
request
  → ExceptionTrap        (turns unhandled ex → 500 internal, logs w/ request id)
  → ProtocolVersionHeader (§7.2: require X-Hyveman-Protocol and range-check it)
  → Gzip                  (Content-Encoding: gzip → decompress; enforce max_batch_bytes on decompressed)
  → ProtocolVersionBody   (§7.2: bind body v and verify header/body lockstep)
  → Auth                  (§7.3: resolve token → source_id + scopes; /health permits optional best-effort introspection; /register uses reg_ token)
  → RateLimit            (§7.4: per-source + global; 429 + Retry-After)
  → Endpoint             (Minimal API delegate)
  → response always carries X-Hyveman-Protocol + "commands": [] on every 2xx and error
```
- **`commands: []` is mandatory** on every 2xx and error response body,
  including `408`, `429`, `5xx`, and `503` responses (PROTOCOL §16). A
  response-writing helper (`IngestResponse.Ok(...)`,
  `IngestResponse.Error(...)`) guarantees this; endpoints never build the
  envelope by hand.
- **`X-Hyveman-Protocol` echoed** on every response (PROTOCOL §9.2).

### 7.2 Versioning middleware (PROTOCOL §3)
- Read `X-Hyveman-Protocol` (int) before body decompression. Missing →
  `400 missing_version`.
- After optional gzip decompression, bind body `v` (int) on JSON requests.
  The bodyless `GET /health` request has no body version field; its required
  protocol header is the request's version carrier. Mismatch between a JSON
  body's header and body `v` → `400 invalid_request` (`version_mismatch` — an
  extension of the spec's "both must carry the same value"; treated as
  malformed).
- Header `v` ∉ supported range (`[1]`) → `400 unsupported_version` with
  `error.supported:[1]`.
- On success, stash `v` in `HttpContext.Items` for endpoints.

### 7.3 Auth middleware (PROTOCOL §4)
- Extract `Authorization: Bearer <token>`. Missing on a required-auth endpoint
  → `401 token_missing`. `GET /health` is special: it permits a missing token
  and best-effort introspects a supplied token, but never turns an invalid
  supplied token into a 4xx response.
- **`ITokenService.ResolveAsync(token)`** → constant-time lookup by
  `token_hash` (§12.1). Outcomes:
  - unknown/malformed hash → `401 token_invalid`
  - `revoked=1` → `401 token_revoked`
  - `consumed_at NOT NULL` (a used `reg_` token) → `410 token_consumed`
  - `expires_at < now` → `401 token_invalid` (treat expired as invalid; agent
    re-registers)
  - an ingest (`agt_`) token has `source_id=NULL` because its source was
    deleted (registration tokens intentionally have no source) →
    `404 unknown_source`
- **Scope check** (per endpoint): `register`-scoped token on a non-`/register`
  endpoint, or `ingest`-scoped token on `/register` → `403 wrong_scope`.
- On success: set `HttpContext.Items["source_id"]` and `["scopes"]`. **The
  body's `source` field and optional `X-Hyveman-Source` header are never used
  for identity** (PROTOCOL §4.2, §9.1). If either corroborating value differs
  from the token's `source_id`, log a warning (possible misconfig) and proceed
  with the token's identity.
- Update `tokens.last_used = now` (best-effort, non-blocking; batched to avoid
  a write per request).

### 7.4 Rate limiting (PROTOCOL §15)
- **Per-source bucket** + **global bucket**, sliding-window or token-bucket in
  a concurrent in-memory store keyed by `source_id` (and `"__global__"`).
  Dimensions: requests/min and bytes/min (from `ServerOptions.ingest`).
- Over-budget → `429 too_many_requests` + `Retry-After` (seconds, computed from
  the bucket) + optional `X-RateLimit-Remaining`.
- A `reg_` token (no `source_id` yet) is bucketed under a small
  `__register__` budget so a leaked install token can't be hammered; the
  single-use + `expires_at` limits blast radius further.
- **Per-source is load-bearing** (PROTOCOL §15): a rogue agent flooding
  `/ingest/logs` gets 429'd without starving other sources.

### 7.5 `POST /register` (PROTOCOL §5)
`RegistrationService.RegisterAsync(req)`:
1. Validate the `reg_` token via `ITokenService` (§7.3). It must have
   `scope=register`, not consumed, not revoked, not expired.
2. **Bound-kind check:** `req.kind` must equal the token's `bound_kind`; else
   `400 invalid_request` (`kind_mismatch`).
3. **Source resolution (reinstall-friendly, PROTOCOL §5.2 step 2):**
   `SELECT id, boot_id FROM sources WHERE kind=@kind AND name=@hostname`.
   If no row exists, insert one with the optional request `boot_id`. If a row
   exists, reuse it when either `boot_id` is absent on one side or both values
   match, and persist a newly supplied `boot_id` when the stored value is NULL.
   If both non-null boot IDs differ, treat the request as a distinct physical
   host, append a disambiguator (`HOST01-2`), and insert a new source with the
   final name and boot ID. The operator can rename it in the UI later. If a
   disambiguated name cannot be allocated, return `409 name_collision` as
   specified by PROTOCOL §5.4. The `(kind,name)` uniqueness constraint is
   retained for the reinstall path.
4. **Mint `agt_` token:** generate 32 random bytes, base32-url-encode with
   `agt_` prefix. Insert `tokens(source_id, token_hash, scopes=['ingest'],
   created=now)`. Return the raw token **once** (only the hash is stored).
5. **Mark `reg_` token consumed:** `UPDATE tokens SET consumed_at=now WHERE
   id=@reg_id`. Subsequent use → `410 token_consumed`.
6. Return 200 `{v, source_id, token, scopes:["ingest"], issued_at,
   commands:[]}`.

All registration database steps run in **one transaction** (atomic: source
reuse/create + token mint + reg consumed). A crash mid-flow leaves neither a
new token nor a consumed reg token.

### 7.6 `POST /ingest/logs` (PROTOCOL §6) — the hot path
`LogIngestService.IngestAsync(source_id, req)`:

1. **Envelope/size pre-checks (whole batch):**
   - decompressed body byte count > `max_batch_bytes` →
     `413 payload_too_large` (agent splits & resends).
   - `items.Length > max_items` → `400 too_many_items` (PROTOCOL §13.3;
     the agent splits the batch based on the stable error code).
   - `items` not homogeneous `kind:"log"` → `400 invalid_request`
     (`wrong_item_kind`).
2. **Per-item validation → partition** into `toStore` and `rejected`:
   - `record_id` present, non-empty, ≤ 128 chars → else `bad_record_id`.
   - `dedup_scope` present (if empty, must be `""`, never null) → else
     `bad_dedup_scope`.
   - `time` parseable UTC ISO-8601 → else `bad_time`.
   - `raw` ≤ server hard cap (16 KiB) → else `raw_oversize`.
   - `message` ≤ 64 KiB → else `message_oversize`.
   - each `fields.*` string ≤ 64 KiB → else `field_oversize`.
   - structural sanity → else `schema`.
   All per-item rejection reasons are **permanent** (`permanent:true`); the
   agent quarantines those items (PROTOCOL §6.4, §6.3).
3. **Idempotent insert** of each valid item into `events`:
   ```sql
   INSERT INTO events
     (source_id, dedup_scope, record_id, time, severity, facility, message,
      fields_json, raw_json, channel, event_id, task, opcode, keywords)
   VALUES (@source_id,@dedup_scope,@record_id,@time,@severity,@facility,@message,
           @fields_json,@raw_json,@channel,@event_id,@task,@opcode,@keywords)
   ON CONFLICT(source_id, dedup_scope, record_id) DO NOTHING;
   ```
   - `source_id` = **token's** `source_id` (never body `source`).
   - Promote Windows fields from `fields.*` to indexed columns
     (`channel,event_id,task,opcode,keywords`); keep the whole `fields` object
     in `fields_json` and `raw` in `raw_json` (PROTOCOL §6.2, §17).
   - `severity` omitted by the agent (Windows Level 0) → default to
     Information (4) at ingest (PROTOCOL §10).
   - Track `accepted` (row inserted) vs `deduped` (conflict, no-op) counts.
     `INSERT … ON CONFLICT DO NOTHING` + checking `changes()` distinguishes
     them per item.
4. **One transaction for the batch** (per-item inserts inside it). A 5xx
   before commit → agent retries the whole batch safely (idempotency collapses
   duplicates on the successful retry). A 4xx non-retryable → the agent
   quarantines; the server has already rolled back, so nothing partial lands.
5. **Alert hook (Phase 2):** after commit, publish a lightweight "new events"
   signal to the alert engine for event-rule evaluation (§9.1). Phase 1 MVP
   raises health + heartbeat alerts only; event rules land in Phase 2, so the
   hook is a no-op stub in Phase 1 (but present so Phase 2 is additive).
6. **Logon-stats aggregation (Phase 2, DESIGN §4.1, §5.2):** for curated
   Security events (4624/4625/4740) on `windows-agent` sources, upsert
   `logon_stats(day, source_id, user, logon_type, success_count,
   failure_count)`. Built in Phase 2; the ingest path already carries the
   `fields.event_data` needed.
7. Return 200 `{v, accepted, deduped, rejected, commands:[]}` with the
   invariant `accepted + deduped + len(rejected) == len(items)`
   (PROTOCOL §6.3).

**Performance target:** the per-item path is one parameterized `INSERT … ON
CONFLICT DO NOTHING` in a single transaction; Dapper + prepared statements +
WAL keep a 1000-item batch well under the agent's retry timeout. No
per-item round-trips.

### 7.7 `POST /ingest/telemetry` (PROTOCOL §7) — latest-wins
`TelemetryService.IngestAsync(source_id, req)`:
- `kind:"heartbeat"` → upsert the latest heartbeat for the source (a
  `agent_heartbeats(source_id PK, sent_at, agent_version, protocol_version,
  os_build, boot_time, uptime_s, degraded, config_hash, counters_json,
  free_disk_json, received_at)` table — latest-wins, **not** idempotent, **not**
  stored in `events`). Arrival resets the "agent silent" timer
  (§9.4, `AgentSilenceWatchdog`).
- `kind:"facts"` → upsert `vms` rows for the source's host (delete-then-insert
  the host's VM set in one transaction; `last_seen` per VM). Persist a
  `health_snapshots`-adjacent facts snapshot for VM sparklines.
- Telemetry does **not** return per-item sub-results (PROTOCOL §7.3); a
  malformed item makes the batch 4xx and is discarded (resends next interval).
- Return 200 `{v, accepted:true, commands:[]}`.

### 7.8 `GET /health` (PROTOCOL §8)
- No `Authorization` → connectivity-only 200 `{v, ok:true, server_time,
  server_version, commands:[]}` (no `source_id`/`scopes`).
- With a valid token → also include `source_id` + `scopes` (lenient variant,
  PROTOCOL §8.1: always 200 if reachable; token validity surfaces via the
  presence of those fields), plus `commands:[]`. A bad token does **not** 4xx
  here.
- `503 unavailable` while starting/not-ready (§3.3), using the standard error
  envelope including `commands:[]`.

---

## 8. Hardware poller (DESIGN §4.2)

### 8.1 Provider abstraction (vendor-neutral)
```
IHardwareProvider                      // one per vendor/transport
  ├─ Task<HostHealth> PollAsync(Host h, CancellationToken ct)
IRedfishProvider : IHardwareProvider
DellRedfishProvider : IRedfishProvider
  (future: HpeIloRedfishProvider, SupermicroRedfishProvider, SnmpProvider)
ComponentNormalizer                    // maps provider output → components/metrics/snapshots
```
`HostHealth` is an intermediate, vendor-agnostic DTO: overall
`rollup_state`, a list of `ComponentState(type,name,state,detail)` and a list
of `MetricSample(name,value,unit)`. `ComponentNormalizer` diff-merges it into
the `components`/`metrics`/`health_snapshots` tables (§8.3). This is the seam
DESIGN §4.2 calls "one *provider*; a future provider can implement the same
model."

### 8.2 Dell Redfish provider (MVP)
Per host (interval 60s, timeout 15s, bounded concurrency from config):
- `GET /redfish/v1/Systems/System.Embedded.1` → `Status.Health` /
  `Status.HealthRollup` (overall + CPU/memory rollup).
- `GET .../Chassis/{id}/Thermal` → temperatures, fans (RPM, status).
- `GET .../Chassis/{id}/Power` → PSU status + wattage (PowerControl +
  PowerSupplies).
- Dell OEM: `DellPhysicalDiskCollection`, `DellArrayDisk`,
  `DellMemoryCollection`, `DellControllerCollection` for disk/PERC/memory
  detail (exact endpoints finalized in the Redfish mapping table — DESIGN
  §14 #3, a separate artifact with sample payloads from one fleet iDRAC).
- **Auth:** the iDRAC credential (`username` + password) is fetched from the
  credentials vault by `hosts.idrac_cred_ref` and used for HTTP Basic auth
  over HTTPS to the iDRAC. Credential is never logged.
- **Normalization map** (state vocabulary → `components.state`):
  iDRAC `Status.Health ∈ {OK,Warning,Critical}` → `ok|warning|critical`;
  absent/unknown → `unknown`. This is the canonical state for health-state
  rules (§9.1).
- **Firmware inventory** (Phase 3, DESIGN §4.2): `SoftwareInventory`
  snapshots and drift reporting are deferred with the Phase 3 migration; the
  Phase 1 poller does not persist firmware inventory.

### 8.3 Poll loop & fault tolerance
- `HardwarePollerService` maintains a per-host `Timer`/`PeriodicAsyncLoop`
  with the configured interval. Polls are independent per host; one host's
  iDRAC being slow/down does not delay others (concurrency cap from config).
- On poll failure (timeout, HTTP error, TLS, bad creds): mark the host's
  components `state='unknown'`, `last_seen=now` only for the *reachable* part;
  record a `host_unreachable` signal that the alert engine turns into a
  heartbeat/iDRAC-unreachable alert (DESIGN §4.4 rule type 3). Do **not**
  nuke existing component rows on a single failed poll — a transient network
  blip must not erase a known-good state.
- **SNMP traps (Phase 2, DESIGN §4.2):** `SnmpTrapService` receives iDRAC
  traps and pushes them through the same `ComponentNormalizer` → alert-engine
  path for sub-second alert latency. Poll remains the reliable baseline.
- **Syslog receive (Phase 3, DESIGN §11 #3):** `SyslogReceiverService` parses
  the separately specified UDP/TCP 514 (and optional TLS 6514) transport into
  the generic event envelope; it is not part of the Phase 1/2 poller.

---

## 9. Alert engine (DESIGN §4.4)

### 9.1 Rule types and evaluation
| Rule type | `rules.type` | Evaluated by | Trigger |
|---|---|---|---|
| Health | `health` | `HealthRuleEvaluator` | component state change (ok→warn/crit) |
| Event | `event` | `EventRuleEvaluator` | new `events` row matching `match_json` (channel+event_id+severity+message regex) |
| Heartbeat | `heartbeat` | `HeartbeatRuleEvaluator` (via `AgentSilenceWatchdog`) | last heartbeat older than threshold; or iDRAC unreachable |
| Threshold | `threshold` | `ThresholdRuleEvaluator` | `metrics` value crosses a bound (temp, watts, disk free) |

- `rules.match_json` encodes the match (e.g. event rule:
  `{"channel":"System","event_id":[6008,7,11,15,51],"min_severity":2,
  "message_regex":"..."}`; threshold rule:
  `{"metric":"temp","op":">","value":75,"unit":"C"}`).
- Evaluation is **event-driven where possible** (component updates and new
  events raise in-process signals) + a 10s **sweep** for time-based rules
  (heartbeat, threshold windows, cooldown expiry).

### 9.2 Deduplication, cooldown, escalation
- **Dedup key** = `(rule_id, host_id, source_id, signature)` with exactly one
  target column populated: health, event, threshold, and iDRAC alerts target a
  `host_id`; agent heartbeat alerts target a `source_id`. `signature` is a hash
  of the rule-specific match identity (component id for health; event key for
  event rules; metric name for threshold). An *active* alert row matching the
  full key is **bumped**: `last_seen=now`, `count++`, severity possibly
  escalated. No new alert/notification if within the rule's `cooldown` window.
- **Per-rule cooldown** (`rules.cooldown`): minimum gap between *notifications*
  for the same dedup key, not between bumps.
- **Escalation levels** (`rules.severity` ∈ `info|warning|critical`): a bumped
  alert may escalate (e.g. warning→critical) and re-notify past cooldown.
- **Status:** `active` → (operator) `acked`/`silenced` → `resolved` (state
  returns to ok / rule no longer matches / maintenance applied). Resolved
  alerts are retained for history; a recurrence opens a new active row.

### 9.3 Maintenance windows & silencing
- `maintenance_windows(host_id, start, end, reason, created_by)` (DESIGN §5.2):
  a `MaintenanceWindowFilter` suppresses *notifications* (and optionally
  *evaluations*, configurable per rule) for a host within an active window.
  Suppressed alerts are still recorded for history (status `silenced`) but not
  sent to channels.
- Ack/silence from the UI (§11) writes `alerts.status` and an `audit_log`
  entry (DESIGN §13 #13).

### 9.4 Heartbeat / agent-silence (DESIGN §4.4 type 3)
- `AgentSilenceWatchdog` (30s sweep) reads `agent_heartbeats.received_at` per
  source; `now - received_at > threshold` (default 180s = 3 missed 60s beats,
  configurable per rule) → raise/refresh a heartbeat alert for that
  `source_id`/host. A subsequent heartbeat clears it.
- The same watchdog covers **iDRAC unreachable** (poller reports unreachable
  for `N` consecutive polls) as a heartbeat-class alert on the host.

### 9.5 Notification fan-out
- A rule → channels via `rule_channels` (DESIGN §5.2, §13 #13). When an alert
  fires/escalates past cooldown, `NotificationDispatcher.Enqueue` builds a
  `Notification` (alert summary + channel list) and fans out.
- The dispatcher is a **durable queue**: enqueueing an alert creates one
  `notification_queue` row per channel, and an in-process channel drains due
  rows. A 5xx from Telegram retries with backoff across restarts; a permanent
  4xx (bad token) marks the channel failed and surfaces in the UI + audit log
  (DESIGN §13 #13).

---

## 10. Notification dispatcher (DESIGN §4.4)

### 10.1 `INotifier` providers
```
INotifier { Kind; Task<NotifyResult> SendAsync(Notification n, ChannelConfig c, CancellationToken); }
TelegramNotifier   // Telegram Bot API: POST sendMessage (outbound-only, no inbound webhook/port)
WebhookNotifier    // generic HTTP POST JSON (covers Teams if a M365 Workflows webhook ever exists)
SmtpNotifier       // optional, Phase 3 (DESIGN §4.4, §13 #4)
```
- **Telegram (primary, DESIGN §13 #4):** bot token + chat ID stored in the
  vault under the channel's `config_ref`. `sendMessage` with a formatted alert
  body (severity emoji, host, component/message, time, deep-link to the UI
  alert). Markdown/HTML per Telegram limits; messages > 4096 chars split.
- **Webhook:** POSTs a stable JSON schema
  (`{alert_id, rule, host, severity, message, first_seen, count, url}`) to the
  configured URL; bearer/header secret from the vault. Retries 5xx. The
  default URL policy rejects loopback, link-local, private-network, and cloud
  metadata destinations to prevent SSRF; an explicitly configured allowlist is
  required for intentional internal webhook targets.
- **No inbound listener / no inbound webhook** for Telegram (DESIGN §4.4) —
  outbound HTTPS only, works behind NAT.

### 10.2 Channel config & secrets
- `notification_channels.config_ref` → `credentials` row (DESIGN §13 #13);
  `INotifier` receives the **decrypted** `ChannelConfig` only in-memory, via
  `ICredentialVault`. Secrets are never written to logs, never returned by the
  admin API in plaintext (only `label`/`kind`/`created`/`rotated`).

---

## 11. Web UI (Blazor Server) (DESIGN §4.5, §8)

### 11.1 Pages
- **`/auth/setup`** — first-run passkey registration wizard (§11.3). Served
  *only* when `passkeys` is empty *and* the request is localhost/trusted
  network. Once ≥1 passkey exists, never served again.
- **`/auth/login`** — passkey-only login ceremony (WebAuthn assertion) →
  sets the session cookie.
- **`/dashboard`** — fleet grid: one tile per host with rolled-up health
  (Hardware / OS / Hyper-V), green/amber/red. Click-through to per-host.
- **`/host/{id}`** — component table (disks, DIMMs, PSUs, fans, temps with
  sparklines from `health_snapshots`/`metrics`), recent critical events, VM
  list with heartbeats (`vms`).
- **`/search`** — log search: time/host/channel/severity/event-id/free-text
  (FTS) filters; saved searches.
- **`/alerts`** — rules CRUD, alert history, acknowledge/silence, channel
  config, maintenance windows.
- **`/admin`** — host registration (issue `reg_` token + one-liner), iDRAC
  credentials vault, retention settings, passkey management, server
  health/version, backup status.

### 11.2 Blazor Server specifics
- Server-rendered components over a SignalR circuit; no SPA, no JS build.
- All UI data access goes through the **same repository layer** as ingest/poller
  (§6.4) — the UI is just another consumer. Read-only pages use read-only
  SQLite connections (WAL → no reader/writer contention).
- **No secrets to the browser:** credential/channel pages show only metadata;
  editing a secret writes a new vault entry and returns success/failure, never
  the value.

### 11.3 First-run setup wizard (DESIGN §8)
- When `passkeys` is empty: any page redirects to `/auth/setup` *if* the
  request origin is localhost or a configured trusted network; otherwise
  refuse with instructions to run the setup locally. The wizard registers the
  first passkey (platform authenticator or security key). After ≥1 passkey,
  the route 404s.

---

## 12. Auth & security (DESIGN §7, §8, PROTOCOL §4)

### 12.1 Agent tokens (`ITokenService`)
- **Hashing:** SHA-256 of the raw token (constant-time compare on lookup via a
  `token_hash` index). Raw tokens are never stored (PROTOCOL §4.1, DESIGN
  §5.1).
- **Prefixes** are parsed only to classify the token; SHA-256 is computed over
  the complete raw token, including its prefix. Lookup is hash-only; kind is
  read from the matched row, and raw tokens are never stored.
- **Scopes** are stored as a JSON array (`["ingest"]`) and checked per
  endpoint (§7.3). Future `command-pickup` scope is additive (PROTOCOL §4.3,
  §16) — no schema change needed.
- **Lifecycle:** minted by `/register`; long-lived; admin-driven rotation =
  revoke + re-register (reinstall reuses the `source_id`, PROTOCOL §5.2);
  `last_used` updated (batched). Revocation sets `revoked=1` → `401
  token_revoked` on next use.
- **`reg_` tokens** (admin-issued, single-use): `scope='register'`,
  `source_id=NULL`, `bound_kind`, `expires_at` (e.g. +7d), `consumed_at=NULL`.
  Admin issuance writes an `audit_log` entry (DESIGN §13 #13). One per host
  enrollment.

### 12.2 Passkey web auth (`PasskeyService`) (DESIGN §8)
- **Passkey-only:** WebAuthn/FIDO2, multiple passkeys per install (≥2
  recommended: phone + Windows Hello). No passwords, no TOTP, no backup codes.
- **RP ID** read from `config/rp_id.txt` (explicit, not runtime hostname —
  DESIGN §8, §13 #14). Same-hostname restore keeps passkeys valid;
  cross-hostname restore requires `auth reset` → empty `passkeys` → wizard.
- **Session:** auth cookie `HttpOnly; Secure; SameSite=Strict`, 14-day
  sliding expiry (DESIGN §8). Sliding = each authenticated request extends
  expiry.
- **Library:** Fido2 (passwordless.dev) for the server-side ceremony; small JS
  interop in Blazor for `navigator.credentials`. Requires a secure context
  (valid cert + real hostname — available, DESIGN §8).
- **Console-only fallback:** `hyveman-server auth reset|list-passkeys|
  remove-passkey` CLI subcommands require local admin and operate on the DB
  directly; `auth reset` clears `passkeys` → re-triggers the wizard. No
  remote recovery path (DESIGN §8).
- **Rate limiting** on `/auth/*` (cheap insurance; passkey auth is
  challenge-response with no guessable code space).

### 12.3 Credentials vault (`ICredentialVault`, AES-GCM) (DESIGN §7, §13 #13)
- `AesGcmCredentialVault` wraps the server key K (§5.2). `credentials.blob_encrypted`
  stores `nonce(12B) ‖ ciphertext ‖ tag(16B)`; `kind`/`label`/`created`/`rotated`
  are plaintext metadata.
- **Single vault** for idrac creds + notification channel secrets + any
  `server.json` secret refs (§5.4). No plaintext secrets in `hosts`,
  `notification_channels`, or config.
- **Rotation:** `ICredentialVault.Rotate(label, newBlob)` re-encrypts and sets
  `rotated=now`. Key rotation (re-wrapping all blobs under a new K) is a CLI
  op (`hyveman-server vault rotate-key`) — rare, audit-logged.
- **Not machine-scoped DPAPI** (DESIGN §7) so restored backups are readable
  elsewhere; the trust anchor for the snapshot is "snapshot + K" (DESIGN §9).

### 12.4 TLS & transport
- HTTPS only (PROTOCOL §2). TLS ≥ 1.2, prefer 1.3.
- **Certificate sources** (§5.3): a static cert via `tls.cert_path` (own CA or a
  certbot-style external renewal) — or **automatic Let's Encrypt** via
  `tls.lets_encrypt` (§5.5), which provisions and renews without external tooling.
  Invalid/missing static cert → fail to start (except Development, which falls back to
  the ASP.NET Core dev cert).
- `ca_path` pinning is not server-side (that's the agent).
- `HSTS` enabled on the UI host; ingest clients (agents) ignore it (they pin
  the cert/hostname themselves).

### 12.5 Audit log (DESIGN §13 #13)
- `audit_log(time, actor, action, target_kind, target_id, detail_json)` for
  all config changes (rule/channel/host create-edit-delete, reg token issued,
  passkey add/remove, maintenance window set, retention changed, vault
  rotation, auth ceremonies) and **fed into the alert pipeline** — i.e. audit
  entries are themselves visible in the UI and can drive alert rules (DESIGN
  §7, §4.4). Actor = the passkey identity (UI) or `system`/`cli`/`agent:
  <source_id>`.

---

## 13. Background services: retention & backup (DESIGN §9, §13 #10)

### 13.1 Retention (`RetentionService`, daily)
```sql
DELETE FROM events WHERE time < @cutoff;   -- @cutoff = now - events_days
-- FTS sync triggers remove the matched events_fts rows automatically
PRAGMA incremental_vacuum;                  -- if vacuum_after_purge (reclaims freed pages incrementally)
```
- `events_days` default 365 (DESIGN §10 Phase 3, "configurable from day one").
  The 5-year archive goal is deferred (DESIGN §13 #6); the config knob is the
  only day-one requirement.
- Also prunes `metrics`, `health_snapshots`, and `audit_log` using their
  configured retention knobs, and reaps `resolved` alerts older than the
  configured alert-history window.

### 13.2 Backup (`BackupService`, daily at `backup.time_local`) (DESIGN §9)
1. **`VACUUM INTO '<data_dir>/backup/daily/hyveman-YYYYMMDD.db'`** — SQLite's
   own consistent-copy API, crash-safe while the server keeps running. This is
   the **only** safe hot-copy method (copying live `.db`/`-wal`/`-shm` is not
   WAL-safe — DESIGN §9).
2. **Retention ladder:** promote `daily→weekly` (latest of the week) and
   `weekly→monthly` (latest of the month) on schedule; delete beyond
   `keep_daily=7 / keep_weekly=4 / keep_monthly=12` (DESIGN §9).
3. **Not separately encrypted** (DESIGN §9): the snapshot contains only
   ciphertext secrets (AES-GCM via §12.3, copied through by `VACUUM INTO`).
   Restore needs **snapshot + key K** (K swept up by the existing VM/file
   backup covering the data folder). Reintroduce a passphrase-derived KEK
  wrapping K only if the offsite target becomes untrusted (e.g. generic S3) —
  deferred.
4. **Offsite:** the operator's existing VM/file backup schedule sweeps the
   `backup/` folder (and the data folder for K). Optional S3 target later.
5. **Restore drill (early, DESIGN §9):** restore a snapshot onto a scratch
   machine with K, point a fresh server at it, verify the UI works. This is a
   documented one-time procedure, not code.

---

## 14. Observability of the server itself

- **Serilog** structured logs to `<data_dir>/logs/server-*.json` (rolling,
  `file_retain_days`) + console when run interactively. Levels from
  `ServerOptions.logging`. **No secrets logged** (a logging enricher masks
  `Authorization`, `token`, `password`, `blob_*` fields).
- **`GET /health`** doubles as the server liveness check (PROTOCOL §8).
- **Own metrics (in-memory, surfaced in `/admin`):** ingest
  `accepted/deduped/rejected` counters, per-source rate-limit state, poller
  success/latency per host, alert/notification counts, DB size, WAL size,
  last backup time/size. (Phase 3 may add a Prometheus scrape endpoint —
  DESIGN §10 — behind a feature flag; MVP shows these in the admin UI.)
- **Crash handling:** unhandled exceptions in a hosted service are logged at
  `Fatal`; worker supervision applies a short backoff before restarting the
  affected loop. A process-level failure is recovered by Windows Service
  Recovery in service mode (or the console supervisor in development), and a
  `server_degraded` audit entry is written so it surfaces in the alert pipeline
  (DESIGN §7).

---

## 15. Deployment

### 15.1 Packaging & install (DESIGN §6, §13 #2)
- **Self-contained single-file exe** (`dotnet publish -p:PublishSingleFile
  -p:SelfContained -r win-x64`), trimmed conservatively (Blazor + reflection
  mean aggressive trimming is risky; default trim is fine). Targets .NET 8
  on the DESIGN minimum of Windows Server 2019 or later. `win-x64` is the
  default packaging RID, not a runtime limit — `-r linux-x64` produces the
  identical single-file binary for Linux hosts.
- `install.ps1` (one-liner): copies the exe to `%ProgramFiles%\Hyveman\server`,
  creates the data dir (`%ProgramData%\Hyveman\server`) with correct ACLs
  (`SYSTEM` + `Administrators`), registers the Windows service
  (`sc.exe create HyvemanServer binPath= ...`), sets it to auto-start, and
  starts it. Reads `server.json` if present, else writes defaults.
- **Manual install for now** (DESIGN §13 #2); kept GPO/MSI-deployable later:
  silent flags, idempotent install, token-based agent registration so scripted
  rollout needs no interaction (DESIGN §4.5).

### 15.2 Run modes
- **Service** (`Microsoft.Extensions.Hosting.WindowsServices`): production on Windows.
- **Console** (`--console`): development + restore drills (DESIGN §9) +
  `auth reset`/`auth list-passkeys`/`auth remove-passkey`/`vault rotate-key`
  CLI subcommands.
- **Linux (production)**: the same console binary under a systemd unit or a
  container; no Windows-only runtime dependencies. The NTFS ACL hardening in
  `DataDirectory.cs` is a no-op off Windows (`OperatingSystem.IsWindows()`
  guard), and `UseWindowsService()` degrades to a plain console host. The
  data directory, backup, and key-K layout are identical across platforms
  (§9, §14).

### 15.3 Upgrade
- Stop service → replace exe → start. Migrations run on startup (§6.5).
- Backward-compatible with existing agents: protocol v1 additive fields are
  ignored by agents (PROTOCOL §3). A server that drops support for v1 must
  keep serving it until all agents upgrade — but the fleet is small and
  upgrades are coordinated, so a contiguous `[1]` range is fine for now.

---

## 16. Phase mapping (DESIGN §10)

### 16.1 Phase 1 — MVP
- Ingest API: `/register`, `/ingest/logs`, `/ingest/telemetry`, `/health`
  (PROTOCOL v1 fully implemented).
- SQLite store + migrations + retention purge + incremental vacuum.
- Latest-wins heartbeat/facts storage through `/ingest/telemetry`; the agent's
  Hyper-V WMI producer and the VM dashboard remain Phase 2.
- Hardware poller: Dell Redfish health rollup + thermal + power + disks.
- Web UI: fleet overview + per-host component view + log search.
- Alerts: health-state-change + heartbeat-lost → Telegram (+ webhook as 2nd
  channel).
- Passkey auth + first-run wizard + console reset.
- Daily `VACUUM INTO` backup + retention ladder + early restore drill.
- Event-rule hook stub (present, no-op) so Phase 2 is additive.

### 16.2 Phase 2 — depth
- Hyper-V channels + agent-side VM inventory/heartbeat via WMI; the v1
  telemetry endpoint and server-side facts storage are already present from
  Phase 1, while VM dashboard tiles and Hyper-V event rules start here.
- Full alert rule engine: event-match rules, thresholds, cooldowns, silences,
  maintenance windows in the UI.
- SNMP trap receiver from iDRAC (`SnmpTrapService`).
- Logon-stats dashboard (per-user/per-day interactive logon counts per host).
- iSM recommended-install docs.

### 16.3 Phase 3 — nice-to-haves (DESIGN §10)
- In-guest agents (same binary) — server already handles them (envelope is
  source-kind-agnostic).
- Non-Dell providers (generic Redfish, SNMP, HPE iLO) — drop in as
  `IHardwareProvider` (§8.1).
- Firmware inventory + drift reports; scheduled reports.
- Agent auto-update; multi-admin passkeys; ClickHouse store option (introduce
  `ILogStore`, §6.4); Prometheus endpoint; SMTP notifier.
- Syslog receiver transport (DESIGN §11 #3) — Phase 3 agentless Linux path.
- Linux native agent (DESIGN §11 #4) — protocol unchanged.
- Command channel (DESIGN §12) — fill in the reserved `commands` slot
  (PROTOCOL §16); adds a `command-pickup` scope, no protocol bump needed.
- 5-year retention archive strategy (DESIGN §13 #6).
- Automated deployment (GPO/script).

---

## 17. Open / forward

1. **Event-rule evaluation path** — in-process signal vs. a polled cursor over
   a high-water mark on `events.id`. Signal is lower latency; a cursor is more
   robust to restarts and avoids dropping events if the engine is slow.
   Decide in Phase 2; Phase 1 ships the stub.
2. **`ILogStore` introduction** — deferred (DESIGN §13 #10, §6.4). The
   repository seams make it a contained refactor; don't build the abstraction
   until a second backend validates the API.
3. **TLS certificate automation** — Let's Encrypt via an ACME client
   (e.g. `Pekmark.ACME` or external `win-acme`) vs. own CA. Setup-wizard
   integration TBD.

---

## Appendix A — Schema DDL (v1)

Authoritative for the server's SQLite layout. Implements DESIGN §5.1/§5.2 +
the extensions noted in §6.2. Run by `DbMigrator` (§6.5).

```sql
PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;

-- ── Identity, logs, auth (DESIGN §5.1) ────────────────────────────────
CREATE TABLE sources (
  id          TEXT PRIMARY KEY,                      -- src_<ulid>
  kind        TEXT NOT NULL,                          -- windows-agent|linux-agent|syslog-feed
  name        TEXT NOT NULL,                          -- hostname (agents) / feed name
  boot_id     TEXT,                                   -- optional opaque host fingerprint from registration
  created     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  UNIQUE(kind, name)                                  -- reinstall reuse (PROTOCOL §5.2)
);

CREATE TABLE hosts (
  id               TEXT PRIMARY KEY,                  -- host_<ulid>
  source_id        TEXT,                               -- nullable: no-agent hosts / syslog feeds (DESIGN §13 #12)
  name             TEXT NOT NULL,
  kind             TEXT,                               -- dell-poweredge|generic|...
  idrac_url        TEXT,
  idrac_cred_ref   TEXT,                               -- credentials.label/id; never plaintext
  poll_enabled     INTEGER NOT NULL DEFAULT 1,
  last_poll_at     TEXT,
  last_poll_ok     INTEGER,
  created          TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE SET NULL
);

CREATE TABLE tokens (
  id           TEXT PRIMARY KEY,                      -- tok_<ulid>
  source_id    TEXT,                                   -- NULL for reg_ tokens or orphaned agt_ tokens
  token_hash   TEXT NOT NULL,                          -- sha256(raw); unique index
  scopes       TEXT NOT NULL DEFAULT '[]',             -- JSON array: ["ingest"]|["register"]|...
  created      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  last_used    TEXT,
  revoked      INTEGER NOT NULL DEFAULT 0,
  consumed_at  TEXT,                                    -- reg_ single-use (PROTOCOL §5.2)
  expires_at   TEXT,                                    -- optional; reg_ tokens expire
  bound_kind   TEXT,                                    -- reg_ tokens bind a source kind
  FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE SET NULL
);
CREATE UNIQUE INDEX idx_tokens_hash ON tokens(token_hash);

CREATE TABLE events (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  source_id    TEXT NOT NULL,
  dedup_scope  TEXT NOT NULL DEFAULT '',               -- DESIGN §13 #11
  record_id    TEXT NOT NULL,                          -- opaque; epoch-prefixed post-clear (PROTOCOL §11.1)
  time         TEXT NOT NULL,                          -- UTC ISO-8601
  severity     INTEGER,                                -- per-kind scale (PROTOCOL §10); NULL→default at ingest
  facility     TEXT,                                   -- provider name (Windows) / RFC5424 facility
  message      TEXT NOT NULL,
  fields_json  TEXT,                                   -- whole fields object
  raw_json     TEXT,                                   -- original XML / raw line, capped
  channel      TEXT,                                   -- promoted (Windows)
  event_id     INTEGER,                                -- promoted (Windows)
  task         INTEGER,
  opcode       INTEGER,
  keywords     TEXT,                                   -- hex string
  ingested_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  UNIQUE(source_id, dedup_scope, record_id),           -- idempotency key (DESIGN §13 #11)
  FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE CASCADE
);
CREATE INDEX idx_events_src_time ON events(source_id, time);
CREATE INDEX idx_events_channel  ON events(channel);
CREATE INDEX idx_events_eventid  ON events(event_id);
CREATE INDEX idx_events_time     ON events(time);      -- retention purge

-- FTS5 external-content over events.message (DESIGN §4.3)
CREATE VIRTUAL TABLE events_fts USING fts5(
  message,
  content='events', content_rowid='id',
  tokenize='unicode61 remove_diacritics 1'
);
CREATE TRIGGER events_ai AFTER INSERT ON events BEGIN
  INSERT INTO events_fts(rowid, message) VALUES (new.id, new.message);
END;
CREATE TRIGGER events_ad AFTER DELETE ON events BEGIN
  INSERT INTO events_fts(events_fts, rowid, message) VALUES('delete', old.id, old.message);
END;
CREATE TRIGGER events_au AFTER UPDATE ON events BEGIN
  INSERT INTO events_fts(events_fts, rowid, message) VALUES('delete', old.id, old.message);
  INSERT INTO events_fts(rowid, message) VALUES (new.id, new.message);
END;

-- ── Hardware health (DESIGN §5.2) ─────────────────────────────────────
CREATE TABLE components (
  id         TEXT PRIMARY KEY,
  host_id    TEXT NOT NULL,
  type       TEXT NOT NULL,                            -- cpu|memory|disk|controller|psu|fan|temp|...
  name       TEXT NOT NULL,
  state      TEXT NOT NULL,                            -- ok|warning|critical|unknown
  detail     TEXT,
  last_seen  TEXT NOT NULL,
  UNIQUE(host_id, type, name),
  FOREIGN KEY (host_id) REFERENCES hosts(id) ON DELETE CASCADE
);
CREATE INDEX idx_components_host ON components(host_id);

CREATE TABLE health_snapshots (
  host_id         TEXT NOT NULL,
  time            TEXT NOT NULL,
  rollup_state    TEXT NOT NULL,                       -- ok|warning|critical|unknown
  components_json TEXT NOT NULL,
  PRIMARY KEY(host_id, time),
  FOREIGN KEY (host_id) REFERENCES hosts(id) ON DELETE CASCADE
);

CREATE TABLE metrics (
  host_id  TEXT NOT NULL,
  time     TEXT NOT NULL,
  name     TEXT NOT NULL,                              -- temp|psu_watts|disk_free_pct|...
  value    REAL NOT NULL,
  unit     TEXT,
  FOREIGN KEY (host_id) REFERENCES hosts(id) ON DELETE CASCADE
);
CREATE INDEX idx_metrics_host_time ON metrics(host_id, time, name);

CREATE TABLE vms (
  id            TEXT PRIMARY KEY,
  host_id       TEXT NOT NULL,
  name          TEXT NOT NULL,
  state         TEXT,                                  -- on|off|paused|saved|other|unknown (PROTOCOL §7.1)
  heartbeat_ok  INTEGER,                               -- 1|0|NULL
  last_seen     TEXT,
  cpu_pct       REAL,
  mem_mb        INTEGER,
  UNIQUE(host_id, name),
  FOREIGN KEY (host_id) REFERENCES hosts(id) ON DELETE CASCADE
);

-- Firmware inventory is introduced by the Phase 3 migration together with
-- drift reporting (DESIGN §10); it is not part of the Phase 1 schema.

-- ── Telemetry (latest-wins; not idempotent; PROTOCOL §7) ──────────────
CREATE TABLE agent_heartbeats (
  source_id        TEXT PRIMARY KEY,                   -- latest-wins per source
  sent_at          TEXT NOT NULL,
  received_at      TEXT NOT NULL,
  agent_version    TEXT,
  protocol_version INTEGER,
  os_build         TEXT,
  boot_time        TEXT,
  uptime_s         INTEGER,
  degraded         TEXT NOT NULL DEFAULT '',
  config_hash      TEXT,
  counters_json    TEXT,
  free_disk_json   TEXT,
  FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE CASCADE
);

-- ── Alerting (DESIGN §5.2) ────────────────────────────────────────────
CREATE TABLE rules (
  id        TEXT PRIMARY KEY,
  name      TEXT NOT NULL,
  type      TEXT NOT NULL,                             -- health|event|heartbeat|threshold
  match_json TEXT NOT NULL,
  severity  TEXT NOT NULL,                             -- info|warning|critical
  cooldown  INTEGER NOT NULL DEFAULT 300,              -- seconds between notifications
  enabled   INTEGER NOT NULL DEFAULT 1,
  created   TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE TABLE alerts (
  id         TEXT PRIMARY KEY,
  rule_id    TEXT NOT NULL,
  host_id    TEXT,
  source_id  TEXT,                                     -- heartbeat rules target a source
  severity   TEXT NOT NULL,
  signature  TEXT NOT NULL,                            -- dedup grouping key (§9.2)
  first_seen TEXT NOT NULL,
  last_seen  TEXT NOT NULL,
  count      INTEGER NOT NULL DEFAULT 1,
  status     TEXT NOT NULL DEFAULT 'active',           -- active|acked|silenced|resolved
  detail_json TEXT,
  FOREIGN KEY (rule_id) REFERENCES rules(id) ON DELETE CASCADE,
  FOREIGN KEY (host_id) REFERENCES hosts(id) ON DELETE SET NULL,
  FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE SET NULL
);
CREATE INDEX idx_alerts_status ON alerts(status);
CREATE INDEX idx_alerts_rule   ON alerts(rule_id, status);
CREATE INDEX idx_alerts_dedup  ON alerts(rule_id, host_id, source_id, signature, status);

CREATE TABLE rule_channels (
  rule_id    TEXT NOT NULL,
  channel_id TEXT NOT NULL,
  PRIMARY KEY(rule_id, channel_id),
  FOREIGN KEY (rule_id)    REFERENCES rules(id)                ON DELETE CASCADE,
  FOREIGN KEY (channel_id) REFERENCES notification_channels(id) ON DELETE CASCADE
);

CREATE TABLE notification_channels (
  id          TEXT PRIMARY KEY,
  name        TEXT NOT NULL,
  kind        TEXT NOT NULL,                            -- telegram|webhook|smtp
  config_ref  TEXT NOT NULL,                           -- credentials.id/label; no plaintext
  enabled     INTEGER NOT NULL DEFAULT 1,
  created     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE TABLE notification_queue (                       -- durable retry queue for v1 (§9.5)
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  alert_id    TEXT NOT NULL,
  channel_id  TEXT NOT NULL,
  attempts    INTEGER NOT NULL DEFAULT 0,
  next_at     TEXT NOT NULL,
  last_error  TEXT,
  FOREIGN KEY (alert_id)    REFERENCES alerts(id)                ON DELETE CASCADE,
  FOREIGN KEY (channel_id)  REFERENCES notification_channels(id) ON DELETE CASCADE
);

CREATE TABLE logon_stats (                              -- DESIGN §4.1, §5.2 (Phase 2)
  day            TEXT NOT NULL,                         -- YYYY-MM-DD
  source_id      TEXT NOT NULL,
  user           TEXT NOT NULL,
  logon_type     INTEGER,
  success_count  INTEGER NOT NULL DEFAULT 0,
  failure_count  INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY(day, source_id, user, logon_type),
  FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE CASCADE
);

CREATE TABLE maintenance_windows (
  id         TEXT PRIMARY KEY,
  host_id    TEXT NOT NULL,
  start      TEXT NOT NULL,
  end        TEXT NOT NULL,
  reason     TEXT,
  created_by TEXT,
  FOREIGN KEY (host_id) REFERENCES hosts(id) ON DELETE CASCADE
);

-- ── Auth & secrets (DESIGN §5.2, §7, §8) ──────────────────────────────
CREATE TABLE passkeys (
  id              TEXT PRIMARY KEY,
  name            TEXT NOT NULL,
  credential_id   TEXT NOT NULL UNIQUE,                -- base64url
  public_key      BLOB NOT NULL,
  sign_count      INTEGER NOT NULL DEFAULT 0,
  created         TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  last_used       TEXT
);

CREATE TABLE credentials (
  id             TEXT PRIMARY KEY,
  kind           TEXT NOT NULL,                        -- idrac|telegram|webhook|smtp|tls|...
  label          TEXT NOT NULL UNIQUE,
  blob_encrypted BLOB NOT NULL,                        -- nonce‖ciphertext‖tag (AES-GCM, §12.3)
  created        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  rotated        TEXT
);

CREATE TABLE audit_log (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  time         TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  actor        TEXT NOT NULL,                          -- passkey name | system | cli | agent:<source_id>
  action       TEXT NOT NULL,
  target_kind  TEXT,
  target_id    TEXT,
  detail_json  TEXT
);
CREATE INDEX idx_audit_time ON audit_log(time);

CREATE TABLE schema_migrations (
  version     INTEGER PRIMARY KEY,
  applied_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
```

**Notes on the DDL:**
- IDs are ULID-encoded `TEXT` (sortable, no UUID-v4 index fragmentation) for
  all non-numeric PKs; `events.id`/`audit_log.id`/`notification_queue.id` use
  `INTEGER AUTOINCREMENT` (dense, FTS content-rowid-friendly).
- `events` has no `host_id` column — a log's host is derived from
  `source_id → hosts.source_id` at query time (DESIGN §13 #12: `sources` is
  the ingest identity, `hosts` is hardware metadata; a source may have no
  host). The UI's "filter by host" resolves host→source_id(s) first.
- `agent_heartbeats` is **not** in DESIGN §5.2 explicitly; it is the
  latest-wins store the protocol requires (PROTOCOL §7.2) — the design's
  heartbeat rules (DESIGN §4.4 type 3) read it via the watchdog.
- `notification_queue` is part of the v1 runtime and schema; pending
  notifications survive server restarts (§9.5).

---

## Appendix B — Decisions introduced by this document

These are implementation decisions made here to remain consistent with
DESIGN §13 and PROTOCOL.md. Listed so they're reviewable; PROTOCOL.md remains
the wire-behavior authority.

| # | Decision | Rationale | Ref |
|---|---|---|---|
| S1 | One .NET process, one Kestrel listener for ingest + Blazor | Simplest for an internal single-server fleet; split allowed by config | §3,§4 |
| S2 | Dapper + raw SQL over EF Core; one data-access style | SQLite-specific SQL (FTS5, ON CONFLICT, VACUUM INTO, WAL pragmas) is first-class; low alloc on ingest hot path | §2,§6 |
| S3 | Minimal API for ingest, Blazor Server for UI | Hot path low overhead; one toolchain for UI (DESIGN §13 #7) | §2,§7,§11 |
| S4 | `tokens` extended with `consumed_at`/`expires_at`/`bound_kind`; `reg_` tokens live in `tokens` with `source_id=NULL`, and deleted sources orphan agent tokens via `SET NULL` | Implements single-use reg tokens + bound-kind (PROTOCOL §4.1,§5.2) while preserving the `unknown_source` error path | §6.2,§7.3,App. A |
| S5 | `sources.(kind,name)` UNIQUE plus optional `boot_id` fingerprint | Makes reinstall source-reuse a single upsert while allowing a distinct physical host with the same hostname to receive a disambiguated source (PROTOCOL §5.2) | §6.2,§7.5 |
| S6 | `events` has no `host_id`; host resolved via `source_id→hosts` at query time | Honors "sources is ingest identity, hosts is hardware metadata" (DESIGN §13 #12) | App. A |
| S7 | `agent_heartbeats` table (latest-wins) | Required by PROTOCOL §7.2 / DESIGN §4.4 heartbeat rules; not enumerated in DESIGN §5.2 | §6.2,§7.7 |
| S8 | FTS5 external-content + triggers over `events.message` | Keeps FTS in sync with inserts + retention deletes automatically | §6.3 |
| S9 | `ILogStore`/`IMetricStore` deferred; MVP uses thin repos | DESIGN §13 #10 says deferred; don't speculate the abstract store before a 2nd backend validates it | §6.4 |
| S10 | In-order SQL migrations with `schema_migrations`; no EF migrator | We don't use EF; additive-only across releases | §6.5 |
| S11 | `too_many_items` returned as HTTP 400 (code stays `too_many_items`) | Follows the status-code table in PROTOCOL §13.3; the agent splits the batch based on the stable error code | §7.6 |
| S12 | Per-item ingest in one batch transaction; `INSERT…ON CONFLICT DO NOTHING` + `changes()` for accepted/deduped | Satisfies the `accepted+deduped+len(rejected)==len(items)` invariant (PROTOCOL §6.3) and idempotent retry | §7.6 |
| S13 | `IHardwareProvider`/`IRedfishProvider`/`DellRedfishProvider` + `ComponentNormalizer` | The vendor-neutral seam DESIGN §4.2 names; providers are additive | §8 |
| S14 | Poller marks components `unknown` on failure but does not delete known-good rows | Transient blips must not erase state; unreachable becomes a heartbeat-class alert | §8.3 |
| S15 | Alert dedup key `(rule_id, host_id, source_id, signature)` with exactly one target column populated; bump semantics and cooldown between *notifications* | Keeps host-targeted and source-targeted heartbeat alerts distinct while implementing DESIGN §4.4 dedup/cooldown/escalation | §9.2 |
| S16 | Durable `notification_queue` table and in-process delivery channel are used in v1 | Alert notifications can retry across server restarts without changing the alert contract | §9.5,App.A |
| S17 | Webhook SSRF guard rejects private/loopback/link-local/metadata destinations by default; explicit allowlists permit intentional internal targets | Prevents an admin mistake becoming an internal probe | §10.1 |
| S18 | File lock on `<data_dir>/state/server.lock` + SQLite file lock guard against two instances on one data dir (a named mutex would be in-process-only on Unix) | Prevents WAL corruption from a misconfigured double-start | §3.3 |
| S19 | `auth reset|list-passkeys|remove-passkey` + `vault rotate-key` CLI subcommands | Implements DESIGN §8 console-only fallback + key rotation | §12.2,§12.3 |

---

## Appendix C — Change log

| Date | Version | Notes |
|---|---|---|
| 2024-08-08 | v1 (draft) | Initial server build contract: process model, module map, data dir, full SQLite schema + FTS5, ingest API implementation of PROTOCOL v1 (register/logs/telemetry/health), Dell Redfish provider, alert engine, notifications, Blazor UI, passkey/token/vault auth, retention + VACUUM INTO backup, deployment, phase mapping, open questions. |

---

*This is the server build contract. The server implements PROTOCOL.md v1 and
the server-side portions of DESIGN.md; changes here that touch wire behavior
are binding and must be reflected in PROTOCOL.md (versioned per PROTOCOL §3).*
