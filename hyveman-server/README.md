# hyveman-server

The always-running Hyveman backend: ingest API, hardware poller, alert engine, storage,
notifications, and the Blazor web UI. Build contract: `../docs/SERVER.md`; wire contract:
`../docs/PROTOCOL.md` (v1).

## Features

### Ingest API (wire protocol v1)

| Endpoint | Purpose |
|---|---|
| `POST /register` | Exchange a single-use `reg_` token for a per-source `agt_` token (ingest scope). Reuses the existing source on reinstall; a `boot_id` mismatch disambiguates (`HOST01-2`, …). Token mint + `reg_` consumption happen in one transaction. |
| `POST /ingest/logs` | Batched log events. Whole-batch size/item caps; per-item validation with per-item rejection (one bad item never fails the batch); idempotent insert keyed on `(source_id, dedup_scope, record_id)`; response reports `accepted`/`deduped`/`rejected`. Windows `fields.*` are promoted to indexed columns (`channel`, `event_id`, `task`, `opcode`, `keywords`). |
| `POST /ingest/telemetry` | `heartbeat` items (latest-wins upsert: agent version, uptime, counters, `free_disk`, `degraded`, `config_hash`) and `facts` items (VM inventory replace per host). |
| `GET /health` | 503 until startup migrations complete (readiness gate); lenient auth — a valid token adds `source_id`/`scopes` but never 4xxes. |

Request pipeline (order matters): exception trap (500 + `X-Hyveman-RequestId`, no stack
traces) → `X-Hyveman-Protocol` header check → optional gzip decompression (bounded by
`max_batch_bytes`) → header/body version lockstep → Bearer auth + scope check → rate
limiting. Every response carries `v` and the mandatory `commands: []` slot.

### Security

- **Tokens**: raw tokens are never stored — only SHA-256 hashes. `reg_` tokens are
  single-use, scope-checked, and optionally kind-bound; `agt_` tokens carry the ingest
  scope. Revocation, expiry, and (batched) `last_used` tracking supported.
- **Web auth**: passkey-only (WebAuthn/FIDO2) with a first-run setup wizard
  (`/auth/setup`, served only while no passkey exists, restricted to localhost or
  `web.trusted_networks`); multiple passkeys per install, sign-counter tracking.
- **Sessions**: HMAC-signed cookie derived from server key K, HttpOnly + Secure +
  SameSite=Strict, 14-day sliding expiry.
- **Credential vault** (`Auth/CredentialVault.cs`): AES-GCM encryption of all secrets
  (iDRAC credentials, Telegram/webhook secrets, TLS cert password refs) under key K in
  `config/key`; per-label create/rotate/delete, audited.
- **Rate limiting**: token buckets per source + a global budget + a small `__register__`
  budget, counting both requests and body bytes; 429 + `Retry-After`; idle buckets reaped.

### TLS & Let's Encrypt

Two certificate sources, mutually exclusive:

- **Static cert** — `tls.cert_path` (+ `cert_password` or `cert_password_ref` vault
  label), as before.
- **Let's Encrypt (ACME v2, http-01)** — `tls.lets_encrypt.enabled: true`. The server
  registers an ACME account, issues, and **auto-renews** certificates (90-day validity,
  renewed at `renew_days` before expiry) in a background service — no certbot, no cron.

```jsonc
"tls": {
  "lets_encrypt": {
    "enabled": true,
    "domains": ["hyveman.example.com"],   // public DNS names; no wildcards (http-01)
    "email": "admin@example.com",          // ACME account contact
    "staging": false,                       // true = Let's Encrypt staging (test, no rate limits)
    "renew_days": 30,                       // renew when < 30 days remain (1..89)
    "http_port": 80                         // http-01 challenge listener (+ http→https redirect)
  }
}
```

How it works:

- The challenge endpoint `/.well-known/acme-challenge/<token>` is served on a plain-HTTP
  listener on `http_port`; all other plain-HTTP traffic gets a 308 redirect to HTTPS.
  **Port `http_port` must be reachable from the internet on the server's public IP** (a
  reverse proxy forwarding the challenge path is fine).
- State lives in `<data_dir>/certs/`: `account-key.pem` (ACME account key, registered
  once) and `cert.pfx` (issued chain, PFX password derived from server key K — nothing
  extra to back up; the data-dir backup already covers it).
- Until the first order lands, the server serves a short-lived self-signed bootstrap
  certificate so HTTPS works end-to-end from the very first boot. Issuance/renewal
  failures never block startup — they retry with backoff (1 min → 1 h) and are logged.
- A certificate is served per-handshake via `ServerCertificateSelector`, so a renewal
  swaps in atomically with no listener restart.

### Hardware polling

- Per-host iDRAC/Redfish polling with bounded concurrency and a per-host timeout — one
  slow or down host never delays others. Dell PowerEdge provider reads the system health
  rollup, CPU/memory summary, chassis temps, fans, PSUs, power draw, and Dell OEM
  disk/controller/memory collections. Vendor-agnostic `IHardwareProvider` seam for future
  providers.
- Poll failures mark components `unknown` (never deleted), track consecutive-failure
  counts, and signal the alert engine after `alerts.idrac_unreachable_polls` consecutive
  misses. Metrics samples and health snapshots are stored per poll.

### Alert engine

- Default rules seeded on first run: *Hardware component degraded* (health), *Agent
  silent* (heartbeat), *iDRAC unreachable* (heartbeat/unreachable). Health rules diff
  component states against a startup baseline (no spurious first-run fires); heartbeat
  rules track silence per source.
- Dedup + bump (`count`, `last_seen`), warning→critical escalation, per-rule cooldown
  **between notifications** (persisted, restart-safe), maintenance windows (alerts
  recorded as `silenced`, no notifications), resolution on recovery, and a 30s
  agent-silence watchdog (also covers sources that never heartbeated).

### Notifications

- Durable per-channel queue with exponential backoff (max 1 h) that survives restarts;
  permanent 4xx → dropped, audited, and surfaced in the admin UI.
- **Telegram** notifier: HTML messages, 4096-char splitting, severity emoji.
- **Webhook** notifier: JSON POST with optional `Authorization: Bearer <secret>`;
  **SSRF guard** rejects loopback/private/link-local/metadata targets unless allowlisted
  (`notifications.webhook.allow_private` / `allowed_hosts`).

### Web UI (Blazor Server)

- `/dashboard` — fleet overview with health rollups and agent state.
- `/host/{id}` — components, VMs, temps/power, recent events, active alerts.
- `/search` — FTS5 full-text log search with source/channel/event-ID/severity/time filters.
- `/alerts` — rule CRUD, active alerts, history, rule→channel assignment.
- `/admin` — hosts & registration (`reg_` token issuance), credential vault, notification
  channels (with failure status), retention/backup actions, passkeys, server health.
- `/auth/setup`, `/auth/login` — passkey registration and login ceremonies.

### Maintenance & observability

- **Retention**: daily purge of events, metrics, health snapshots, audit log, and resolved
  alerts per `retention.*` days; optional `incremental_vacuum`.
- **Backup**: daily `VACUUM INTO` snapshot at `backup.time_local`, weekly/monthly
  promotion, 7/4/12 keep ladder, audit + metrics recorded.
- **Observability**: Serilog JSON rolling files with a secret-masking enricher;
  in-memory counters (ingest accepted/deduped/rejected, poller latency, alerts,
  notifications) surfaced in `/admin`; full audit log; single-instance guard.

## Requirements

- .NET 8 (LTS), **cross-platform**: Windows Server 2019+ (Windows service, the
  default production form) or Linux (same binary via systemd/Docker in console
  mode). Only the agent is Windows-bound.

## Tests

```powershell
dotnet test tests/Hyveman.Server.Tests
```

113 unit/integration tests against a real migrated SQLite sandbox (temp dir per test):

- **Auth** — token hashing, AES-GCM credential vault (round-trip, tamper/wrong-key
  rejection, rotate), token resolution (revoke/consume/expire), passkey session cookies
  (HMAC tamper, expiry, removal).
- **Rate limit** — token-bucket capacity, byte budget, global vs per-key budgets, refill,
  idle reaping.
- **Notifications** — SSRF guard (loopback/private/link-local/metadata/CGNAT/DNS-failure),
  Telegram format + send classification, webhook payload/bearer/classification, and the
  durable queue: backoff on transient failure, drop + audit on permanent failure,
  disabled channels, missing vault config.
- **Storage** — migration application/idempotence/full schema, event idempotency
  `(source_id, dedup_scope, record_id)`, epoch-prefixed record IDs, FTS5 search,
  field promotion, FTS query sanitization.
- **Ingest** — registration flow: agt_ minting, single-use reg_ consumption, reinstall
  reuse, boot_id disambiguation, kind binding, scope checks.
- **Alerts** — baseline diffing (no first-run fires), fire/resolve, dedup bump,
  warning→critical escalation, heartbeat silence + recovery, maintenance-window
  silencing, iDRAC-unreachable signals, persisted notification cooldown.

Tests call `DapperConfig.Register()` via a module initializer (the server normally does
this in `Program.cs`) and seed the `sources` rows their FK constraints require.

## Build & publish

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained   # single-file exe in bin/Release/.../publish
```

## Install (production)

```powershell
# from the publish folder, elevated:
powershell -ExecutionPolicy Bypass -File install.ps1
```

Creates `%ProgramData%\Hyveman\server` (ACLs: SYSTEM + Administrators), writes a default
`config/server.json`, registers the `HyvemanServer` Windows service, starts it.
`install.ps1 -Uninstall` stops/removes the service (data preserved).

**Linux — two install modes (systemd):**

```bash
# per-user service (default; no root, runs in your login session):
./install-linux.sh            # publishes, generates a self-signed cert, installs + starts
./install-linux.sh --lets-encrypt admin@example.com --domain hyveman.example.com   # ACME mode

# system-wide service (root; /etc/systemd/system, dedicated 'hyveman' system user):
sudo ./install-linux.sh --system --lets-encrypt admin@example.com --domain hyveman.example.com

./install-linux.sh --uninstall  # stops/removes the service + binary (data preserved)
```

**User mode** — no root required: installs to `~/.local/lib/hyveman/server`, data in
`~/.local/share/hyveman/server`, service `hyveman-server` under `systemctl --user`
(linger enabled so it survives logout). Default HTTPS port 8443 (user services can't bind
<1024). With `--lets-encrypt`, the http-01 challenge listener defaults to port 80, which a
per-user service can't bind — use `--http-port` + a reverse proxy forwarding
`/.well-known/acme-challenge/`, or install with `--system` instead.

**System mode (`--system`, root)** — for always-on fleet backends: installs to
`/opt/hyveman/server`, data in `/var/lib/hyveman/server`, unit in
`/etc/systemd/system/hyveman-server.service` (`multi-user.target`). The service runs as a
dedicated unprivileged `hyveman` system account with `AmbientCapabilities=
CAP_NET_BIND_SERVICE` — it can bind ports 80/443 (default HTTPS port 443, and the LE
http-01 challenge on port 80) without running as root. Override paths/port with
`--data-dir`, `--install-dir`, `-p/--port`; env overrides `HYVEMAN_PORT`,
`HYVEMAN_DATA_DIR`, `HYVEMAN_INSTALL_DIR`.

Idempotent; an existing `config/server.json` is never overwritten. See
`install-linux.sh --help` for `--cert`, `--exe`, `--no-start` options.

## Run (development / console)

```powershell
dotnet run -- --data-dir <dir> --urls https://localhost:8443
# or, with an existing config:
hyveman-server.exe --data-dir <dir>
```

TLS: set `tls.cert_path` (+ `cert_password` or `tls.cert_password_ref` → vault label) in
`config/server.json` — or enable `tls.lets_encrypt` for automatic Let's Encrypt
provisioning/renewal (see [TLS & Let's Encrypt](#tls--lets-encrypt)). In Development,
Kestrel falls back to the ASP.NET Core dev cert.

## Data directory

```
<data_dir>/
  hyveman.db            SQLite (WAL + FTS5)
  config/server.json    options (snake_case)
  config/key            AES-GCM server key K (generated first run; keep backed up!)
  config/rp_id.txt      WebAuthn RP ID (set explicitly before registering passkeys)
  certs/                Let's Encrypt state: account-key.pem + issued cert.pfx (ACME mode)
  backup/daily|weekly|monthly   VACUUM INTO snapshots (7/4/12 ladder)
  logs/                 Serilog JSON rolling files
```

**Backup rule:** back up the whole data folder (snapshot + key K). Restore = point a fresh
install at the folder. Losing `config/key` loses all vault secrets.

## First run

1. Start the server, browse to it **from the server itself** (localhost) — the first-run
   passkey wizard appears at `/auth/setup` while `passkeys` is empty.
2. Register ≥1 passkey (a second one is recommended).
3. In `/admin`: add iDRAC credentials (label `host01-idrac`, value = two lines:
   `username` then `password`), register the host (iDRAC URL + credential label), add a
   Telegram/webhook channel + credential, issue a `reg_` token and run the agent installer
   on the target host.

## Console subcommands (local admin)

```
hyveman-server --data-dir <dir> auth list-passkeys
hyveman-server --data-dir <dir> auth remove-passkey
hyveman-server --data-dir <dir> auth reset            # clears passkeys → re-triggers wizard
hyveman-server --data-dir <dir> vault rotate-key      # re-wraps all vault blobs under a new K
```

## Protocol smoke test

```bash
curl -k https://host:443/health -H "X-Hyveman-Protocol: 1"
curl -k -X POST https://host:443/register \
  -H "X-Hyveman-Protocol: 1" -H "Authorization: Bearer reg_..." \
  -H "Content-Type: application/json" \
  -d '{"v":1,"kind":"windows-agent","hostname":"HOST01"}'
```
