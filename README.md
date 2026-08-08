# Hyveman

Windows Server log aggregator & hardware health monitor for a small fleet
(6–7 physical hosts, < 50 guest VMs) — Dell PowerEdge + AMD EPYC + Windows
Server 2019+ / Hyper-V.

Two deployables, both C# / .NET 8 (self-contained single-file exes):

| Project | Role | Build contract |
|---|---|---|
| [`hyveman-agent/`](hyveman-agent/README.md) | Windows service per host: Event Log tail, Hyper-V WMI facts, HTTPS ingest push | [`docs/AGENT.md`](docs/AGENT.md) |
| [`hyveman-server/`](hyveman-server/README.md) | Always-on backend: ingest API, iDRAC poller, alert engine, SQLite store, Telegram/webhook notifications, Blazor UI — cross-platform (Windows service or Linux systemd/Docker) | [`docs/SERVER.md`](docs/SERVER.md) |

System design contract: [`docs/DESIGN.md`](docs/DESIGN.md) · Wire protocol:
[`docs/PROTOCOL.md`](docs/PROTOCOL.md) (v1).

```
 HOST (Windows Server + Hyper-V)                    BACKEND (always-on)
 ┌───────────────────────────────┐                   ┌───────────────────────────────┐
 │ hyveman-agent ── EvtSubscribe │                   │ Ingest API (HTTPS)            │
 │   System/Application/Security │                   │   /register /ingest/logs      │
 │   (curated) / Hyper-V channels│── HTTPS push ────►│   /ingest/telemetry /health   │
 │   bookmarks + spool + retry   │                   │ Hardware poller (iDRAC        │
 │   WMI: VM heartbeat/stats ────┼── facts ─────────►│   Redfish, Dell provider)     │
 └───────────────────────────────┘                   │ Alert engine (health +        │
                                 ┌──────────────────►│   heartbeat rules)            │
                                 │  Redfish poll     │ SQLite (WAL + FTS5)           │
                          ┌──────┴──────┐            │ Telegram / webhook notify     │
                          │ iDRAC       │            │ Blazor UI (passkey-only)      │
                          └─────────────┘            └───────────────────────────────┘
```

## Status

- **Phase 1 (MVP) — complete.** Ingest + SQLite store + retention; agent tail,
  heartbeats, bookmarks, one-liner install; iDRAC Redfish poller; Blazor UI;
  alerts → Telegram/webhook; passkey-only auth; daily `VACUUM INTO` backups.
- **Phase 2 — mostly done.** Hyper-V WMI VM inventory/heartbeat (agent +
  UI), maintenance windows, alert cooldowns + escalation, agent-silence
  watchdog, durable notification queue. Missing: event/threshold rules,
  logon stats, SNMP trap receiver.
- **Phase 3 — not started.** In-guest agents, non-Dell providers, syslog/Linux
  support, firmware drift, command channel, SMTP, ClickHouse, Prometheus.

See [Remaining work](#remaining-work) for the full list.

## Implemented

### Agent (`hyveman-agent/`)

- `EvtSubscribe` push subscriptions per channel with persisted bookmarks —
  no gaps/duplicates on the happy path; crash-safe via
  `(source_id, dedup_scope, record_id)` idempotency and epoch-prefixed
  `record_id` after a channel clear (DESIGN §13 #15).
- Durable bounded spool (`max_bytes` + `min_free_bytes`, drop-oldest, atomic
  writes); gzip HTTPS ingest to `/ingest/logs` + `/ingest/telemetry`.
- Batch shaping: `batch_max_events`/`batch_max_age_ms`, `max_batch_bytes`
  chunking, `max_raw_bytes` truncation with `…hyveman-truncated:` marker.
- Send-outcome state machine: 2xx → delete; permanent 4xx per-item rejects →
  quarantine; 413/`too_many_items` → split in half and resend; credential 4xx
  (`token_invalid`/`token_revoked`/`wrong_scope`/`unknown_source`) → keep the
  spool, surface `auth_rejected`, retry slowly so a rotated token self-heals;
  408/429/5xx/network → exponential backoff (1–60 s, ±20 % jitter, honors
  `Retry-After`).
- Curated Security log (4624 logon types 2/10, 4625, 4740) — no full Security
  forwarding (DESIGN §13 #5).
- Job Object containment: 256 MiB memory kill-cap, 25 % CPU hard-cap, Below
  Normal priority — the agent cannot degrade the host.
- WMI facts via `root\virtualization\v2` (serialized, timeout-bounded,
  stale-cached): VM list/state, heartbeat, CPU%, memory.
- Heartbeats with counters (events/batches sent & dropped, spool bytes/files,
  queue depth, WMI timeouts, send errors), `config_hash`, priority-ordered
  sticky `degraded` flag (`spool_full`, `overrun`, `auth_rejected`,
  `quarantined`, `wmi_degraded`, `channel_reset`).
- Config hot-reload of the safe subset; `GET /health` / `POST /register`
  bootstrap (`reg_` token → long-lived `agt_` token); non-fatal startup
  health probe.
- Lifecycle events in the `HyvemanAgent` Application-log source with a
  self-collect allowlist (no recursion).
- TLS: pinned CA (`backend.ca_path`) or `validate_cert=false` lab mode.
- `build.ps1`, idempotent `install.ps1` (dirs/ACLs, config, Hyper-V channels
  opt-in, EventLog source, SCM service with recovery, preflight),
  `uninstall.ps1`.
- Tests: **97 unit/property tests passing** (`tests/Hyveman.Agent.Tests/`);
  fault-injection harness start (`tests/Hyveman.Agent.FaultHarness/`,
  mock backend); `tests/EvtSubscribeProbe/` dev probe.

### Server (`hyveman-server/`)

- **Ingest API (protocol v1):** `POST /register` (single transaction: reuse
  source on reinstall, `boot_id` disambiguation, `reg_` → `agt_`),
  `POST /ingest/logs` (per-item rejection, idempotent insert, response
  `accepted`/`deduped`/`rejected`), `POST /ingest/telemetry` (latest-wins
  heartbeat + facts), `GET /health` (readiness gate). Middleware pipeline:
  exception trap → protocol header check → gzip → auth/scope → rate limiting.
- **Security:** tokens stored as SHA-256 hashes only; `reg_` single-use and
  kind-bound; revocation/expiry/`last_used`. Passkey-only web auth
  (WebAuthn/FIDO2) with first-run localhost wizard, multiple passkeys,
  sign-counter tracking; HMAC session cookie (HttpOnly/Secure/SameSite=Strict,
  14-day sliding); AES-GCM credential vault (iDRAC creds, Telegram/webhook
  secrets) under key K in `config/key`; token-bucket rate limiting.
- **TLS:** static `tls.cert_path` cert **or** built-in Let's Encrypt
  (ACME v2, http-01) — auto-issuance + renewal, challenge listener on port 80,
  atomic per-handshake cert swap, state in `certs/` (account key + PFX
  protected by key K).
- **Hardware polling:** per-host iDRAC/Redfish with bounded concurrency and
  per-host timeouts; Dell PowerEdge provider (health rollup, CPU/memory,
  chassis temps, fans, PSUs, power draw, Dell OEM disk/controller/memory);
  vendor-neutral `IHardwareProvider` seam. Failures mark components
  `unknown`, tracked, and signal the alert engine after
  `alerts.idrac_unreachable_polls` misses.
- **Alert engine:** seeded default rules (health, agent silent, iDRAC
  unreachable); dedup + bump, warning→critical escalation, per-rule
  notification cooldown (persisted), maintenance windows (silenced),
  resolution on recovery, 30 s agent-silence watchdog.
- **Notifications:** durable per-channel queue with exponential backoff
  (survives restarts; permanent 4xx dropped + audited); Telegram (HTML,
  4096-char splitting, severity emoji); webhook with SSRF guard
  (loopback/private/link-local/metadata blocked unless allowlisted).
- **Web UI (Blazor Server):** `/dashboard`, `/host/{id}` (components, VMs,
  temps/power, recent events, active alerts), `/search` (FTS5 full-text),
  `/alerts` (rule CRUD + rule→channel assignment), `/admin` (hosts,
  registration tokens, vault, channels, retention/backup, passkeys, server
  health), `/auth/setup` + `/auth/login`.
- **Maintenance:** daily retention purge (events/metrics/snapshots/audit/
  resolved alerts + optional `incremental_vacuum`); daily `VACUUM INTO`
  backup with 7/4/12 keep ladder; Serilog JSON rolling files with
  secret-masking enricher; in-memory counters surfaced in `/admin`; full
  audit log; single-instance guard.
- Console subcommands: `auth list-passkeys/remove-passkey/reset`,
  `vault rotate-key`.

## Verification

- `dotnet build` — clean on both projects (0 warnings / 0 errors).
- `dotnet test tests/Hyveman.Agent.Tests` — 97/97 passing.
- `dotnet test tests/Hyveman.Server.Tests` — 113/113 passing (unit + integration
  against a real migrated SQLite sandbox; covers auth/vault, rate limiting, SSRF guard,
  notifiers, notification queue, migrations, event idempotency + FTS5 search,
  registration flow, and the alert engine).

## Remaining work

### Phase 2 (partial — the near-term backlog)

| Item | DESIGN ref | Notes |
|---|---|---|
| Event-match rules (channel + event ID + level + regex) | §4.4 rule type 2 | Currently an explicit no-op in `AlertEngineService` |
| Threshold rules (temp, PSU wattage, disk free) | §4.4 rule type 4 | Not implemented |
| Logon stats aggregation (per-user/per-day logon counts) + dashboard | §5.1, §13 #5 | `logon_stats` table not created |
| SNMP trap / syslog receiver from iDRAC | §4.2, Phase 2 | Only a seam/comment so far |
| iSM recommended-install docs (hardware events via log pipe) | Phase 2 | Docs only |

### Phase 3 (not started)

- In-guest agents (same binary) for VM event logs · §11
- Non-Dell providers (generic Redfish, SNMP, HPE iLO) · §11 #6
- Syslog receiver (UDP/TCP 514, RFC 3164/5424) and Linux agent · §11 #3/#4
- Firmware inventory + drift reports; scheduled reports · Phase 3
- Agent auto-update · Phase 3
- Agent command channel (config push → Hyper-V VM control), scope-capable
  tokens, `commands` protocol slot reserved · §12
- SMTP notifier · Phase 3
- ClickHouse/other store behind the (deferred) `ILogStore` interface · §13 #10
- Prometheus endpoint for Grafana interop · Phase 3
- 5-year retention: yearly export/archive of filtered events · Phase 3
- Automated deployment (GPO/script) · Phase 3
- Snapshot encryption re-introduced only if offsite target becomes untrusted
  (S3) · §9

### Design decisions already baked in (no rework needed later)

- Generic ingest envelope (not Windows-shaped) + source-kind-agnostic
  idempotency key · §11 #1
- `commands: []` slot in every response; protocol versioned via
  `X-Hyveman-Protocol` header + body `v` · §12 #5
- Single-data-directory rule; backup = copy folder + key K · §9

## Quick start

- **Agent install** (per host, see [`hyveman-agent/README.md`](hyveman-agent/README.md)):
  `./install.ps1 -BackendUrl https://hyveman.example.lan:8443 -InstallToken reg_<admin-issued> [-EnableHyperV]`
- **Server** (see [`hyveman-server/README.md`](hyveman-server/README.md)):
  publish, `install.ps1` for the Windows service, or `dotnet run -- --data-dir <dir> --urls https://localhost:8443`.
  First run: register a passkey from localhost, then add iDRAC credentials,
  register hosts, configure channels, issue `reg_` tokens.
- **Protocol smoke test** (`docs/PROTOCOL.md`): `curl -k https://host:443/health -H "X-Hyveman-Protocol: 1"`
