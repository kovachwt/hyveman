# Hyveman — Windows Server Log Aggregator & Hardware Health Monitor

Design document (draft v2)

## 1. Goals

1. **Primary:** at-a-glance **hardware health** of the server fleet — Dell PowerEdge
   servers with AMD EPYC CPUs running Windows Server + Hyper-V, plus any other
   Windows servers.
2. Centralized collection and search of **Windows Event Logs** (hosts and,
   optionally, guest VMs).
3. **Alerting & notification** (Telegram first, generic webhook, optional
   email/Teams) on hardware faults, critical events, and agent silence.
4. Web frontend for dashboards, log search, and alert configuration.
5. Minimum supported OS: **Windows Server 2019**.

**Expected scale (per decision):** 6–7 physical hosts, < 50 guest VMs.
This is a small fleet — SQLite storage and a single server instance are
comfortably sufficient.

Non-goals (for now): full SIEM/security analytics, Linux support, application
performance tracing, long-term compliance archiving.

## 2. Research summary

### 2.1 Hardware health sources

| Source | What it gives us | Verdict |
|---|---|---|
| **Dell iDRAC Redfish API** (out-of-band, agentless) | `Status.HealthRollup` (OK/Warning/Critical) for whole system, per-component status: CPUs, DIMMs, disks, PERC controllers, PSUs, fans, temps, power draw, firmware inventory. Dell OEM schemas (`DellPhysicalDisk`, `DellMemory`, ...) add detail. | **Primary hardware health source.** Works even when the OS is down. Fleet is EPYC-based (14th gen+) = iDRAC9, fully supported; note iDRAC7/8 Redfish is a limited subset (only relevant for legacy boxes). |
| **iDRAC push** (SNMP traps, syslog, email alerts) | Instant notification of hardware faults without polling | **Secondary / complement** — nice for sub-second alert latency; poll is the reliable baseline. |
| **Dell iDRAC Service Module (iSM)** in the OS | Mirrors iDRAC hardware events into the Windows Event Log (`iDRAC`/iSM channels); WMI passthrough | Install alongside our agent → hardware events arrive through the normal log pipeline. Small, official, supported. |
| **Dell OMSA (OpenManage Server Administrator)** | Legacy in-band hardware monitoring | **EOL 2024** (support until 2027). Do not build on it. |
| **AMD EPYC specifics** | E-SMI / AMD-SMI are Linux-only; AMDuProf is a profiler | On Windows, CPU temps/power/health come via iDRAC sensors. No extra tooling needed. |
| **Windows own signals** | Unexpected shutdown (6008), disk errors (7/11/15/51), storahci/percsas driver events, memory diagnostics, cluster events | Captured by the event-log agent. |

### 2.2 Log collection options (Windows Server 2019+)

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Windows Event Forwarding (WEF/WEC)** | Built-in, agentless, push | Requires a Windows collector + WinRM/Kerberos setup per machine, subscription XML hard to manage dynamically, forwards to an event log not to an API | Viable fallback; not the primary path |
| **Custom agent (Win32 Event Log API)** | Full control, `EvtSubscribe` push or bookmark pull, exact channel/level filtering, can attach metadata, single HTTPS protocol | We own maintenance of the agent | **Recommended** |
| Existing collectors (Winlogbeat, NXLog, Wazuh, Fluent Bit) | Mature | External ecosystem/schema, heavy agents, alerting/dashboard still to build | Rejected for now |

### 2.3 Hyper-V

- Interesting channels: `Microsoft-Windows-Hyper-V-VMMS-Admin`,
  `-Worker-Admin`, `-Compute-Operational`, `-Config-Operational`,
  `-StorageVSP-Admin`, `-High-Availability-Admin`, `-Image-Management-Operational`.
  Several are *operational/debug* channels and must be enabled by the agent or installer.
- **Guest VM monitoring without agents**: Hyper-V WMI (`root\virtualization\v2`)
  exposes per-VM **heartbeat** (Integration Services) and per-VM CPU/memory/disk
  counters. This answers "is the VM alive?" from the host.
- Optionally install the same lightweight agent *inside* guest VMs for their
  event logs — it's the same binary, just registered as a regular source.

### 2.4 Reference architectures surveyed

Graylog, Grafana Loki, Wazuh, SigNoz, VictoriaLogs — all general-purpose, all
require building the hardware-health layer ourselves anyway. Conclusion: build a
lean, purpose-fit system; keep the **storage layer behind an interface** so we
can swap SQLite → ClickHouse if the fleet grows.

## 3. Architecture

```
 ┌────────────────────────── HOST (Windows Server, Hyper-V) ─────────────────────────┐
 │                                                                                    │
 │  ┌──────────────┐   Windows Event Log API        ┌────────────────────────────┐   │
 │  │ hyveman-agent │◄──── System/Application/Security/Hyper-V-*/iSM channels    │   │
 │  │  (service)   │────── HTTPS push (JSON, batched, retry/backoff, bookmarks) ──┼──►│
 │  └──────┬───────┘                                                              │   │
 │         │ WMI (root\virtualization\v2): VM heartbeat, per-VM stats             │   │
 └─────────┼──────────────────────────────────────────────────────────────────────┘   │
           │                                                                          │
           │   (iDRAC network)                                                        │
 ┌─────────▼────────┐  Redfish poll + trap/syslog receive                             │
 │      iDRAC       │───────────────────────────────┐                                 │
 └──────────────────┘                               │                                 │
                                                    ▼                                 │
 ┌───────────────────────────────── BACKEND (always-on service) ──────────────────────┐
 │  ┌────────────┐  ┌────────────────┐  ┌──────────────┐  ┌──────────────────────┐   │
 │  │ Ingest API │  │ Hardware poller │  │ Alert engine │  │ Store (SQLite/FTS5,  │   │
 │  │ (HTTPS)    │  │ (Redfish/SNMP)  │  │ (rules,      │  │  pluggable)          │   │
 │  └────────────┘  └────────────────┘  │  dedupe,     │  └──────────────────────┘   │
 │                                      │  notify)     │         ▲                   │
 │                                      └──────┬───────┘         │                   │
 └─────────────────────────────────────────────┼──────────────────┼───────────────────┘
                                               ▼                  │
                                   Telegram / webhook / SMTP    │
                                   (SNMP-trap-out later)   ┌───┴────────┐
                                                              │ Web frontend│
                                                              │ (dashboards,│
                                                              │  search,    │
                                                              │  alerts UI) │
                                                              └────────────┘
```

Three independently built and deployed components:

1. **`hyveman-agent`** — small Windows service installed on each host (and
   optionally inside guest VMs).
2. **`hyveman-api`** — the always-running .NET backend API: ingest API,
   hardware poller, alert engine, storage, and notification dispatch. Runs
   on a server of your choice; **cross-platform by design** — Windows Server
   2019+ (Windows service, the default) or Linux (systemd/Docker, console
   mode). Only the agent is Windows-bound.
3. **`hyveman-web`** — a React + TypeScript single-page application (SPA),
   built by Vite into static assets. It is served independently by IIS,
   nginx, Caddy, or equivalent and communicates with `hyveman-api` over
   HTTPS. The preferred deployment puts the SPA and the `/api` reverse proxy
   behind one public HTTPS origin, avoiding CORS and authentication-cookie
   complications. The frontend contains no backend secrets and is not hosted
   inside the .NET process.

## 4. Component design

### 4.1 Agent

- **Event log collection:** `EvtSubscribe` per configured channel with a
  persisted bookmark (resume after restart, no duplicates, no gaps). Batched
  HTTPS POST; local disk spool (bounded size; oldest dropped with a metric
  when full) if backend is unreachable.
- **Default channels (hosts):** System, Application, **Security (curated —
  logon tracking only, see below)**, Hyper-V channels (enabled via explicit
  installer option; the agent never enables or modifies channels at runtime),
  iSM/iDRAC channel if present, Microsoft-Windows-FailoverCluster*
  if clustered, storage driver channels.
- **Security log policy (decided):** *no full Security log forwarding.* The
  agent subscribes with an XPath filter for:
  - **4624** successful logon, only `LogonType 2` (interactive/console) and
    `LogonType 10` (RDP) — i.e. *real human logins*, not service/program
    logons (types 3, 4, 5, …)
  - **4625** failed logon (all types)
  - **4740** account lockout
  Server-side, these are additionally aggregated into **per-user/per-day
  logon counts** (see §5) so the interesting question — "who logged on where,
  how often" — is cheap to answer. Note: audit policy "Audit Logon Events"
  must be enabled on hosts (default on servers).
- **Filtering at source:** min level (default: Warning+), plus per-channel
  include/exclude event-ID lists. Keep bandwidth tiny.
- **Hyper-V enrichment:** periodic WMI scan → VM list, state, heartbeat status,
  per-VM CPU/RAM counters; sent as structured *facts* (not logs).
- **Self-reporting:** heartbeat message with agent version, OS build, uptime,
  free disk. Backend raises "agent silent" alert if heartbeat missed N intervals.
- **Auth:** per-agent token issued by backend during registration; TLS to backend.
- **Footprint:** single self-contained exe (Windows service), ~10s install,
  configurable via local file or pushed config from backend. Runs as
  **`LocalSystem`** for now (no dedicated service account — §13 #17).

### 4.2 Server — hardware poller

- Per registered Dell host: poll iDRAC Redfish on an interval (default 60s):
  - `GET /redfish/v1/Systems/System.Embedded.1` → overall `Health`/`HealthRollup`
  - `GET .../Chassis/.../Thermal` → temps, fans
  - `GET .../Chassis/.../Power` → PSU status + wattage
  - Dell OEM: physical disks, PERC, memory
- Normalize into a **component health model** (see §5.2) that is
  vendor-neutral: the Dell collector is one *provider*; a future provider can
  implement the same model over SNMP, HPE iLO Redfish, Supermicro Redfish, etc.
- Optionally receive **SNMP traps / syslog** from iDRAC for push alerts.
- Firmware/driver inventory snapshot per poll (drift detection = nice-to-have).

### 4.3 Server — ingest & storage

- Ingest: HTTPS JSON, **two endpoints** (§13 #16) — `/ingest/logs` for
  event batches (idempotent, spooled by the agent) and `/ingest/telemetry`
  for heartbeats + Hyper-V facts (latest-wins, not idempotent). Both
  token-authenticated. `/ingest/logs` is **idempotent via
  `(source_id, dedup_scope, record_id)`**: `record_id` (TEXT) is assigned
  by the source; `dedup_scope` (TEXT, non-null) scopes the uniqueness. For
  the Windows agent `dedup_scope = channel` and `record_id` is the
  channel's EventRecordID, epoch-prefixed after a channel clear/wrap so
  reset RecordIDs don't collide with pre-clear records (§13 #15). For a
  future syslog feed `dedup_scope = ''` and `record_id` is a per-source
  sequence. The envelope carries both fields so non-Windows sources are
  not forced into a Windows-shaped key (§13 #11).
- **Store (default): SQLite in WAL mode with FTS5.**
  - Logs: **one append-only `events` table** (not monthly partitions),
    indexed on `(source_id, time)`, `(channel)`, `(event_id)`; FTS5 on
    rendered message. At this fleet volume (tens of MB/day worst case) a
    `DELETE WHERE time < ?` plus periodic `PRAGMA incremental_vacuum` is
    cheap, and a global unique index makes cross-boundary retry dedup
    correct. If 5-year hot retention ever demands partitioning, the
    `ILogStore` interface isolates the switch (§13 #10).
  - Credentials, notification channels, alerts, rules, config in regular
    tables (see §5).
  - Retention: simple time-based purge job.
- **Storage interface** (`ILogStore`, `IMetricStore` — deferred; SQLite is the
  MVP default) so ClickHouse / VictoriaLogs can replace SQLite later without
  touching ingest/alerts/UI.

### 4.4 Alert engine

- **Rule types:**
  1. *Health rules*: any component state enters Warning/Critical (e.g. predictive
     disk failure, PSU lost, DIMM error).
  2. *Event rules*: matching channel + event ID + level (+ regex on message),
     e.g. Event 6008 unexpected shutdown, disk events 7/11/15, Hyper-V VMMS errors.
  3. *Heartbeat rules*: agent or iDRAC unreachable for X minutes.
  4. *Threshold rules*: temperature, PSU wattage, disk free space.
- **Behaviors:** deduplication window, escalation levels (info/warn/critical),
  per-rule cooldown, maintenance windows (per host), ack/silence from UI.
- **Notification channels (per decision):**
  - **Telegram Bot API — primary.** Setup: create bot via @BotFather, store
    bot token + chat ID. Outbound-only HTTPS `sendMessage` calls; no inbound
    webhook/port needed. Works with a regular personal Telegram account.
  - **Generic HTTP webhook** — covers anything else (and Microsoft Teams
    *if* a M365 work tenant with a Workflows webhook ever becomes available;
    **personal Teams accounts cannot receive incoming webhooks**).
  - SMTP email — optional, low priority.
  - Pluggable `INotifier` interface; a rule fans out to several channels via
    a `rule_channels` join table (§5). Channel secrets (bot tokens, webhook
    URLs) are stored as ciphertext in the credentials vault (§7), not
    plaintext in the channel config.

### 4.5 Web frontend

- **Technology:** standalone React + TypeScript SPA built with Vite. No
  server-side rendering is needed because this is an authenticated operations
  console with no SEO requirement.
- **API integration:** consumes a versioned REST/JSON API exposed by
  `hyveman-api`. The API publishes an OpenAPI contract, and the frontend uses
  a generated TypeScript client rather than handwritten DTOs or fetch calls.
- **Frontend support stack:** React Router for navigation, TanStack Query for
  server-state caching and polling, MUI for accessible tables/forms/dialogs,
  Apache ECharts for health-history charts and sparklines, and
  `@simplewebauthn/browser` for passkey ceremonies.
- **Overview dashboard:** fleet grid — each server a tile with rolled-up
  health (green/amber/red), split into Hardware / OS / Hyper-V. Click-through
  to per-server page: component table (disks, DIMMs, PSUs, fans, temps with
  sparklines), recent critical events, VM list with heartbeats.
- **Log search:** filter by time, host, channel, level, event ID, free-text
  (FTS); saved searches. Filtering, pagination, and full-text search happen
  server-side; the frontend renders the result pages and can virtualize large
  result tables.
- **Alerting UI:** rules CRUD, alert history, acknowledge/silence,
  notification channel config, maintenance windows.
- **Admin:** host registration (agent install token + one-liner), iDRAC
  credentials vault (encrypted at rest), retention settings (single-admin —
  no user management).
- Auth: **passkey-only** (see §8). HTTPS everywhere. The browser receives
  only API data and never receives iDRAC, Telegram, webhook, or other stored
  secret values.
- **Deployment:** the frontend is a separate release artifact consisting of
  static files. Serve the Vite build through IIS, nginx, Caddy, or equivalent;
  preferably route `/api` through the same reverse proxy to `hyveman-api` so
  the browser sees one public origin. The API and frontend may be deployed
  independently, with compatibility governed by the versioned OpenAPI
  contract.

## 5. Data model (core)

### 5.1 Identity, logs, auth
```
sources(id, kind[windows-agent|linux-agent|syslog-feed], name)
hosts(id, source_id NULL, name, kind, idrac_url, idrac_cred_ref, ...)
  -- hosts is hardware metadata; no-agent hosts and syslog feeds have a sources
  -- row with no hosts row, and are polled/ingested accordingly
tokens(id, source_id, token_hash, scopes[ingest|command-pickup], created, last_used, revoked)
  -- hashed, never plaintext; scope-capable per §12 #5; syslog feeds auth via
  -- token or IP-allowlist (§11.3)
events(id, source_id, dedup_scope, record_id, time, severity, facility, message,
       fields_json, raw_json,
       -- Windows-event attributes as indexed columns when present (§11):
       channel, event_id, task, opcode, keywords)
  -- UNIQUE(source_id, dedup_scope, record_id); dedup_scope TEXT NOT NULL DEFAULT ''
  -- record_id TEXT, source-kind-agnostic; time is UTC
```

**Field mapping (Windows → envelope):** `severity` = Windows Level enum;
`facility` = the Windows provider name (NOT `channel`); `channel` stays in
its own column. For syslog, `severity`/`facility` are RFC 5424 values (§11).

### 5.2 Hardware health (vendor-neutral) & alerting
```
components(id, host_id, type[cpu|memory|disk|controller|psu|fan|temp|...],
           name, state[ok|warning|critical|unknown], detail, last_seen)
health_snapshots(host_id, time, rollup_state, components_json)  -- history for sparklines
metrics(host_id, time, name, value, unit)                        -- temps, watts, per-volume disk free
vms(id, host_id, name, state, heartbeat_ok, last_seen, cpu_pct, mem_mb)
alerts(id, rule_id, host_id, severity, first_seen, last_seen, count, status, ...)
rules(id, name, type, match_json, severity, cooldown, enabled)
rule_channels(rule_id, channel_id)                               -- many-to-many fan-out
notification_channels(id, name, kind[telegram|webhook|smtp], config_ref, enabled, created)
  -- config_ref points into the credentials vault; no plaintext secrets here
logon_stats(day, source_id, user, logon_type, success_count, failure_count)
maintenance_windows(id, host_id, start, end, reason, created_by)
passkeys(id, name, credential_id, public_key, sign_count, created, last_used)
credentials(id, kind[idrac|telegram|webhook|smtp|...], label, blob_encrypted, created, rotated)
  -- single vault for all secrets (§7); blob_encrypted is AES-GCM ciphertext
audit_log(id, time, actor, action, target_kind, target_id, detail_json)
  -- config changes + auth ceremonies (§7), fed into the alert pipeline
```

## 6. Technology choices

| Piece | Recommendation | Why |
|---|---|---|
| Agent + API | **C# / .NET 8** (LTS), self-contained single-file exes | Native Win32 Event Log APIs (`EvtSubscribe`), WMI via `System.Management`/CimSession`, excellent HTTP/Kestrel, trivial Windows-service hosting; runs fine on Server 2019+. The API is cross-platform — .NET 8 runs the same binary on Linux (systemd/Docker) as on Windows; only the agent is Windows-only |
| Web frontend | **React + TypeScript SPA (Vite)** | Independent static build and deployment, mature ecosystem for data-heavy dashboards, tables, charts, testing, and browser WebAuthn support |
| API contract | **ASP.NET Core REST/JSON + OpenAPI**, with a generated TypeScript client | Keeps frontend/backend DTOs synchronized while allowing independent releases |
| Frontend libraries | React Router, TanStack Query, MUI, Apache ECharts, `@simplewebauthn/browser` | Covers navigation, server-state caching/polling, accessible operations UI, health visualization, and passkey ceremonies |
| Storage | SQLite (Microsoft.Data.Sqlite) + FTS5 | Zero-ops; interface allows swapping to ClickHouse later |
| iDRAC access | HTTPS + Redfish (System.Text.Json), optional SNMP trap listener (Lextm.SharpSnmpLib) | Agentless hardware visibility |
| Packaging | Agent/API: self-contained single-file exes + `install.ps1` one-liner; frontend: Vite static build served by IIS/nginx/Caddy. **Manual install for now** (decided), kept GPO/MSI-deployable later for Windows services | |

Alternative stack if you'd rather not use .NET: Go agent + Go API + React
frontend works too (go-winio/winevt bindings are weaker though — the Windows
Event Log API story is best from C#).

## 7. Security considerations

- TLS everywhere; pinned/self-signed CA option for lab networks.
- Agents authenticate with revocable tokens; iDRAC credentials stored
  encrypted with **AES-GCM using a server key kept in the data directory** —
  deliberately *not* machine-scoped DPAPI, which would make restored backups
  unreadable on a different machine (§9). Secrets are never sent to agents/UI.
- Ingest API validates schema, rate-limits, rejects unknown tokens.
- Security event log forwarding: ship only a curated ID list by default
  (full Security logs are large and sensitive).
- Frontend: **passkey-only login** (WebAuthn/FIDO2) — no passwords, no TOTP,
  no backup codes (decided). Details in §8. Audit log of config changes and
  auth ceremonies, fed into the alert pipeline.

## 8. Authentication (web UI) — decided

Single admin, internet-exposed UI, minimal friction. Final design:

- **Passkeys (WebAuthn/FIDO2) are the only login method.** Multiple passkeys
  registered per installation (decision: at least two — e.g. phone platform
  authenticator + laptop Windows Hello), so losing one device is a non-event.
  No passwords, no TOTP, no backup codes.
- **Requires a proper hostname + valid certificate** (Let's Encrypt or own
  CA) — WebAuthn only runs in a secure context; confirmed available.
- **Session:** persistent auth cookie, `HttpOnly; Secure; SameSite=Strict`,
  **14-day sliding expiry** — effectively never re-login while in regular use.
- **Public origin:** preferably expose the frontend and the `/api` reverse
  proxy under one HTTPS origin. If separate subdomains are used instead,
  configure exact-origin credentialed CORS and keep the explicit WebAuthn RP
  ID and expected origin aligned with the frontend origin; do not move session
  credentials into browser storage.
- **First-run setup wizard:** whenever the `passkeys` table is empty (new
  install *or* after a restore that cleared passkeys), the frontend shows a
  setup page **only when the API permits setup from localhost/trusted
  network**: click "Register passkey", touch sensor, done. Once ≥1 passkey
  exists, setup is never permitted again. (No codes, no clock sync — the
  registration ceremony is self-validating.)
- **RP-ID invariant:** the WebAuthn Relying Party ID is stored explicitly in
  config in the data directory and taken from there — not derived from the
  runtime server hostname. A restore onto the same registered hostname keeps
  existing passkeys valid (no action). A restore onto a different hostname
  leaves `passkeys` populated but unusable (authenticators reject the
  assertion for a different origin); the operator runs `auth reset` to clear
  them, which re-triggers the empty-`passkeys` wizard, then registers fresh
  passkeys. Cross-hostname reuse of passkeys is cryptographically impossible
  and not attempted.
- **Only fallback = console reset:** `hyveman-api auth reset` requires
  local admin on the server and restarts the setup flow (also
  `auth list-passkeys` / `auth remove-passkey`). Appropriate trust anchor:
  local admin already owns the box. There is deliberately **no** remote
  recovery path.
- Rate limiting on auth endpoints as cheap insurance (passkey auth is
  challenge-response; there is no guessable code space).
- **Implementation:** the API uses the .NET `Fido2` library (passwordless.dev)
  to create and validate ceremonies. The React frontend uses
  `@simplewebauthn/browser` to call the browser WebAuthn APIs and sends the
  ceremony responses to the API. The single-admin model is extendable to
  multiple users/credentials later without redesign.

## 9. Backup & restore — decided

- **Design rule: all state lives in one data directory** (SQLite DB, config,
  secrets, backup output). Backup = "copy this folder"; restore = "point a
  fresh install at it". No registry/state scattered elsewhere.
- **Daily hot snapshot via `VACUUM INTO`** (SQLite's own consistent-copy API —
  crash-safe while the server keeps running). Copying the live
  `.db`/`-wal`/`-shm` files is **not** safe (WAL mode, torn copies, no VSS
  writer) — the existing VM/file backup schedule therefore sweeps up the
  snapshot folder instead of the live DB.
- **Retention ladder:** 7 daily / 4 weekly / 12 monthly.
- **Snapshots are not separately encrypted.** iDRAC credentials and other
  secrets in the snapshot are **already ciphertext** via §7's live-DB
  encryption (AES-GCM with the server key K kept in the data directory), and
  `VACUUM INTO` copies that ciphertext through. The snapshot therefore
  contains no plaintext secrets. **Restore requires the snapshot plus K**
  (K is swept up by the same VM/file backup that covers the data folder), not
  the snapshot alone. Separately encrypting the snapshot would only re-wrap
  ciphertext and is **deferred** until the offsite target is no longer
  trusted by the operators (e.g. a generic S3 bucket): then reintroduce a
  passphrase-derived KEK wrapping K for whatever leaves the trust boundary.
- **Offsite:** covered by the existing VM/file backup schedule that already
  includes the backend VM's data folder (decided — trivial to add). Optional
  S3-compatible target later.
- **Restore drill once, early:** restore a snapshot onto a scratch machine
  and verify the UI works. "We have backups" only counts after this.

## 10. Roadmap

**Phase 1 — MVP (see hardware health fast)**
1. API (`hyveman-api`): ingest API + SQLite store + retention job.
2. Agent: event log tail (System/Application, Warning+; curated Security
   logon IDs), heartbeat, bookmarks; `install.ps1` one-liner.
3. Hardware poller: Dell Redfish health rollup + thermal + power + disks.
4. Web (`hyveman-web`, React + TypeScript SPA): fleet overview + per-host
   component view + log search, using the generated OpenAPI client.
5. Alerts: health-state-change + heartbeat-lost → **Telegram** (webhook as
   second channel).
6. Web auth: passkey-only login + first-run setup wizard + console reset (§8).
7. Daily `VACUUM INTO` backup job + retention ladder (§9).

**Phase 2 — depth**
- Hyper-V channels + VM inventory/heartbeat via WMI; VM tiles on dashboard.
- Full alert rule engine (event-match rules, thresholds, cooldowns, silences).
- SNMP trap / syslog receiver from iDRAC.
- Logon stats dashboard (per-user/per-day interactive logon counts per host).
- iSM recommended-install docs so hardware events flow through the log pipe.

**Phase 3 — nice-to-haves**
- In-guest agents (same binary) for VM event logs.
- Non-Dell providers (generic Redfish, SNMP, HPE iLO).
- Firmware inventory + drift reports; scheduled reports.
- Agent auto-update; multi-admin passkeys (if ever more than one user); ClickHouse store option; Prometheus
  endpoint for Grafana interop; SMTP notifier.
- **5-year retention goal (decided, deferred):** retention is a configurable
  policy from day one (default generous, e.g. 365 days hot). Reaching 5 years
  is a later phase: yearly export/archive of filtered events to compressed
  files (still queryable via import or an archive-search view). At this fleet
  size with source-side filtering, 5 years likely fits in tens of GB anyway —
  revisit once real volume numbers exist.
- Automated deployment (GPO/script) once manual rollout becomes tedious.
- **Linux VM support** (see §11): syslog receiver first (agentless), native
  Linux agent later.

## 11. Future Linux support — design implications

Linux guest VMs are expected later. Required compatibility decisions made now
(zero cost today, avoids rework later):

1. **Generic ingest envelope.** The wire format is
   `{source, time, severity, facility, message, record_id, fields{}, raw}` —
   *not* a Windows-event-shaped schema. `record_id` is the source-assigned
   idempotency key (TEXT; Windows = EventRecordID, epoch-prefixed after a
   channel clear/wrap per §13 #15; syslog = per-source sequence, §13 #11). Windows event attributes (channel, event_id, task,
   opcode, keywords) are one mapping onto it; syslog (`facility.severity`,
   RFC 5424 structured data) is another. The `events` table mirrors the
   envelope with Windows-specific attributes in indexed columns + `fields`/
   `raw` JSON for the rest.
2. **Abstract sources.** Registration metadata distinguishes source kinds
   (`windows-agent`, `linux-agent`, `syslog-feed`); dashboards/alert rules
   work off the envelope regardless of kind.
3. **Syslog receiver** (Phase 3): UDP/TCP 514 + optional TLS 6514, RFC
   3164/5424 parsing, token or IP-allowlist auth — the agentless path.
4. **Linux agent** (Phase 3+): tail journald/syslog + heartbeat, same ingest
   protocol and tokens. Implementation language open (.NET 8 runs on Linux
   and lets us reuse most agent code; Go is the alternative for a tiny static
   binary). The protocol is the contract, not the language.
5. **No hardware-health impact**: Linux guests have no hardware to monitor
   (the Hyper-V host covers it), and Hyper-V Integration Services heartbeats
   work for Linux guests too — VM-alive checks need no agent.
6. **Physical Linux hosts someday**: add an IPMI / generic-Redfish provider
   to the already vendor-neutral health model.

## 12. Future: agent command channel — design implications

Discussed and deliberately deferred, but with decisions made now so the
capability can be added without rework:

1. **Direction stays agent-initiated.** The server never opens connections to
   agents. Commands are picked up by the agent on its existing channel
   (heartbeat response carrying work items → long-poll later if needed).
   Network topology and firewall posture never change.
2. **Server-side command subsystem** (later): `commands` table (id, target,
   type, params, issued_by, status, result) + dispatch + pickup endpoint.
3. **Agent-side executor** (later): command classes, starting with config
   push (harmless, shakes out plumbing), then Hyper-V VM control via WMI
   (`Msvm_ComputerSystem` state change = start/stop/shutdown). WinRM from
   server to hosts was considered and rejected (worse trust model).
4. **Security bar — non-negotiable when built:** confirmation dialog for
   destructive ops, full audit trail through the alert pipeline, short-lived
   signed command payloads with nonces (replay protection), and an
   **agent-side allowlist** of permitted command classes in local config
   (limits blast radius if the backend is ever compromised).
5. **Cheap decisions made now:** agent↔server protocol is versioned (JSON
   envelope with `version` field), heartbeat responses may carry work items,
   agent tokens are scope-capable (ingest-only today, command-pickup later).

## 13. Decisions log

| # | Question | Decision |
|---|---|---|
| 1 | Fleet size | ~6–7 physical hosts, < 50 VMs → SQLite + single server confirmed |
| 2 | Deployment | Manual installs for now; keep design compatible with later automated rollout |
| 3 | iDRAC reachability | iDRACs reachable over HTTPS from the backend network → Redfish polling is primary; SNMP/syslog receive optional later |
| 4 | Notifications | **Telegram** (Bot API) primary + generic webhook. Teams deferred: personal Teams accounts cannot receive incoming webhooks (requires M365 work tenant + Workflows webhook) |
| 5 | Security log | Curated subset only: 4624 (logon types 2 & 10), 4625, 4740; aggregated into per-user/per-day logon stats |
| 6 | Retention | 5-year goal, deferred — configurable retention now, archive strategy later |
| 7 | Frontend | React + TypeScript SPA built with Vite, independently deployed as static assets; `hyveman-api` provides the REST/JSON OpenAPI API |
| 8 | Web UI auth | **Passkey-only** (WebAuthn), multiple passkeys registered, 14-day sliding cookie, first-run localhost setup wizard, console-only reset fallback. No passwords, no TOTP, no backup codes. Requires real hostname + valid cert (available) |
| 9 | Backend backup | Daily `VACUUM INTO` hot snapshots (live DB files not copy-safe), **not separately encrypted** (secrets already ciphertext via §7; restore needs snapshot + server key K), 7/4/12 ladder, swept up by the existing VM/file backup schedule; single-data-directory rule; early restore drill. Passphrase-KMK re-added later only if offsite target becomes untrusted (e.g. S3) |
| 10 | Logs storage | Single `events` table (not monthly partitions) + global UNIQUE(source_id, dedup_scope, record_id); `DELETE`-based retention + `incremental_vacuum`. Partitioning deferred behind `ILogStore` (deferred) |
| 11 | Idempotency key | `(source_id, dedup_scope, record_id)` with non-null `dedup_scope`; Windows: `dedup_scope = channel`, `record_id = EventRecordID`; syslog: `dedup_scope = ''`, `record_id = per-source sequence` |
| 12 | Auth identity | `sources` is the ingest identity with `tokens` (hashed, scope-capable); `hosts` is hardware-only metadata, `source_id` NULL for no-agent hosts and for syslog feeds |
| 13 | Secrets & channels | Single `credentials` vault (AES-GCM) used by idrac creds + notification channels; `rule_channels` join for fan-out; `audit_log` for config changes + auth ceremonies; `maintenance_windows` table |
| 14 | Passkey restore | RP ID stored in config (not runtime hostname); same-hostname restore keeps passkeys; cross-hostname restore uses `auth reset` + wizard re-registration (no cross-host passkey reuse) |
| 15 | Idempotency on channel clear/wrap | `EventRecordID` resets to 1 on a channel clear, colliding in `UNIQUE(source_id, dedup_scope, record_id)` with pre-clear records. Agent encodes an epoch: `record_id = "<EventRecordID>"` normally, `record_id = "e<epoch>:<EventRecordID>"` after a detected reset (subscribe returns invalid-bookmark, or RecordID regression observed), bumping the epoch per reset. Fits the opaque-TEXT contract — no backend schema change. Syslog unaffected (per-source sequence doesn't reset this way) |
| 16 | Ingest endpoints | Two JSON endpoints (not one): `/ingest/logs` for event batches (idempotent via the §13 #11/#15 key; agent-spooled & retried; server deduped) and `/ingest/telemetry` for heartbeats + Hyper-V facts (latest-wins, **not** idempotent, not spooled — a missed heartbeat *is* the alert signal, replaying old ones is wrong). Keeps idempotency semantics clean vs mixing a latest-wins stream into an idempotent store |
| 17 | Agent service account | `LocalSystem` for now (no new local accounts to provision/manage on the small fleet). `LocalSystem` already covers Security-log read and WMI access. A dedicated least-privilege account (member of *Event Log Readers*) is a documented later option; the installer keeps the switch simple. Logs/spool/state/config ACLed to `SYSTEM` + Administrators |
| 18 | Agent registration & protocol versioning | `POST /register` exchanges a single-use admin-issued `reg_` token (bound to a source kind) for a long-lived `agt_` ingest-scope token + `source_id`; agent discards the `reg_` token after. Reinstall reuses the existing `source` row by `(kind, hostname)` match and gets a fresh `agt_` token; the old token is left for the operator to revoke. Protocol is versioned: single integer `1` in `X-Hyveman-Protocol` header + body `v`, lockstep; additive optional fields don't bump. See `docs/PROTOCOL.md`. Resolves AGENT §20 #1/#2/#3 |

## 14. Next artifacts for development

This document is the design contract. Before/alongside Phase 1 coding,
produce:

1. **Wire protocol spec** — written (`docs/PROTOCOL.md`, v1). Covers ingest
   endpoint shape (two endpoints per §13 #16), the generic envelope (§11),
   batch format, idempotency keys (source-kind-agnostic `record_id` with the
   epoch scheme per §13 #15, `dedup_scope` per §13 #11), token auth + scopes
   (§13 #12), registration flow (§13 #18), error/retry semantics, `version`
   field rules (§12 #5), and a reserved `commands` field for the deferred
   command channel (§12).
2. **Agent config format** — channels, filters, spool dir, backend URL;
   plus the `install.ps1` registration flow.
3. **Redfish mapping table** — exact endpoints per data point (health
   rollup, thermal, power, disks, OEM extensions) with sample payloads from
   one fleet iDRAC.
