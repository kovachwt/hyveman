# Hyveman — Installation & Operations Guide

Setup guide for the two deployable components:

| Component | What it is | Docs |
|---|---|---|
| `hyveman-api` | Backend: agent ingest, web API, alerts, Redfish, notifications, SQLite | [`docs/API.md`](docs/API.md), [`docs/PROTOCOL.md`](docs/PROTOCOL.md) |
| `hyveman-agent` | Windows service: event-log collection + Hyper-V health facts → HTTPS ingest | [`docs/AGENT.md`](docs/AGENT.md), [`docs/PROTOCOL.md`](docs/PROTOCOL.md) |
| `hyveman-web` *(separate repo)* | Browser frontend consuming `/api/v1` | [`docs/FRONTEND.md`](docs/FRONTEND.md) |

```text
                outbound HTTPS only, no inbound listener
 ┌──────────┐   POST /register, /ingest/logs, /ingest/telemetry, GET /health   ┌──────────────┐
 │  Agent   │ ───────────────────────────────────────────────────────────────► │  hyveman-api │
 │ (per VM) │          bearer token (agt_…), X-Hyveman-Protocol: 1             │  (SQLite)    │
 └──────────┘                                                                    └──────────────┘
                                                                                  ▲
                                                     hyveman-web (browser) ──────┘  /api/v1, session+passkey
```

- The **agent never listens**; the server never initiates connections. Only the
  API's listen port must be reachable from agent hosts.
- The agent protocol paths (`/register`, `/ingest/logs`, `/ingest/telemetry`,
  `/health`) are **fixed by PROTOCOL.md** and must not be renamed or mounted
  under `/api`.
- Everything is UTC. Heartbeat liveness is judged on the **server's receive
  time**, so keep host clocks in sync (NTP) — a skewed agent clock does not
  cause false alerts, but ingested event timestamps will be wrong.

---

## 1. Prerequisites

**Build machine** (any Windows/Linux with internet for NuGet):
- .NET SDK **10.0** (builds `hyveman-api` and `hyveman-agent`)

**Target servers**:
- Windows Server 2019 or 2022 (agent requires the Windows Event Log API;
  the API runs on Windows Server, Linux, or Docker as a console process)
- No runtime install needed if you publish self-contained (recommended below)
- NTFS with ~2 GB free per agent host (spool caps default to 100 MiB but the
  min-free guard is 5 GiB) and ~1 GB for the API data dir

---

## 2. Build the binaries

```powershell
# From the repository root
git clone <your-repo-url> hyveman
cd hyveman

# API — self-contained win-x64 (no runtime needed on the server)
dotnet publish hyveman-api/src/Hyveman.Api -c Release -r win-x64 --self-contained -o C:\deploy\hyveman-api
# Linux target instead: -r linux-x64 (same binary runs as console/systemd)

# Agent — single-file exe (AGENT §11.1)
cd hyveman-agent
./build.ps1            # → hyveman-agent\out\hyveman-agent.exe (~68 MB)
```

Optionally run the test suites first (both must be green):

```powershell
dotnet test Hyveman.Api.sln
dotnet test Hyveman.Agent.sln
```

---

## 3. TLS design — read before installing

The wire protocol **mandates HTTPS** (PROTOCOL §2). Plain HTTP is rejected by
the server except in the explicit dev-only `AllowInsecureHttp` escape hatch
(loopback only, never in production).

Pick **one** topology:

### Option A — reverse proxy (recommended for production)

TLS terminates at a reverse proxy — nginx in this repo's deployment (Caddy
and IIS are equivalents); the API listens on loopback HTTP.

```text
https://hyveman.example.com/register, /ingest/*, /health   → proxy → http://127.0.0.1:5080
https://hyveman.example.com/api/*                          → proxy → http://127.0.0.1:5080
https://hyveman.example.com/                                → hyveman-web static files
```

The concrete configuration for the API VM is the nginx site shipped in this
repo: `deploy/nginx/hyveman-linux-vm.conf` (single file — security headers
inlined). It serves the SPA, proxies `/api/`,
`/register`, `/ingest/`, `/health` to the loopback API, and applies the
cache policy and security headers (install and certbot steps: §5.4). Any
other proxy (Caddy, IIS) must preserve the same path routing,
`X-Forwarded-*` headers, and header rules.

- Use a public CA (Let's Encrypt) or your own corporate CA.
- The agent validates against the **system trust store** by default, so a
  publicly-trusted or AD-published cert needs **no agent-side config**.
- Preserve `X-Forwarded-Proto`/`X-Forwarded-For`; the API trusts loopback
  proxies by default (add remote proxies to `KnownProxies` — see API.md).

### Option B — direct Kestrel HTTPS (lab / small deployments)

Put a certificate (public CA, private CA, or self-signed) and its key in a
PFX, then configure the API's `config.json` (see §4.3):

```jsonc
{
  "ApiListenUrls": "https://0.0.0.0:8443",
  "Kestrel": {
    "Certificates": {
      "Default": { "Path": "C:\\hyveman\\tls\\hyveman.pfx", "Password": "…" }
    }
  }
}
```

**Agent side — certificate trust:**

| Trust model | Agent config | Use when |
|---|---|---|
| System trust store | *(nothing)* | Public CA or AD-issued cert |
| Pin a private/self-signed CA | `backend.ca_path = "C:\\…\\ca.pem"` (PEM or DER) | Your own CA; the agent then builds the chain with `CustomRootTrust` against exactly that file |
| ⚠️ **Never** | `validate_cert = false` | Lab only; the agent logs a loud warning on every start. Never ship this |

The dev loopback setup on this repo uses Option B with a self-signed cert +
`ca_path` pinning — see `devdata/tls/` on the dev machine for the pattern.

---

## 4. Install the API (hyveman-api)

### 4.1 Deploy the binary

```powershell
# Copy the publish folder from the build machine
Copy-Item -Recurse C:\deploy\hyveman-api C:\hyveman\api
```

### 4.2 Create the data directory

The API keeps **all state in one directory** (DESIGN §9): `hyveman.db`
(SQLite, WAL), `vault.key` (AES-GCM server key), `config.json`, `logs/`,
`backup/`. First start creates everything; just create the dir:

```powershell
New-Item -ItemType Directory -Path C:\hyveman\data
```

### 4.3 Configure (`{data-dir}\config.json`)

Loaded after built-in defaults; environment variables (`HYVEMAN_*`) and
command line override it. Full surface: `HyvemanOptions` in
`src/Hyveman.Api/Options.cs`. Minimal production file:

```jsonc
{
  "ApiListenUrls": "http://127.0.0.1:5080",      // behind a proxy
  "PublicOrigin": "https://hyveman.example.com", // browser origin (CSRF/WebAuthn)
  "WebAuthnRpId": "hyveman.example.com",         // passkey relying party id
  "WebAuthnExpectedOrigin": "https://hyveman.example.com",
  "TrustedSetupNetworks": ["10.0.0.0/8"],        // who may run first-run passkey setup
  "HeartbeatSilenceThresholdS": 300,             // agent-silent alert threshold
  "LogRetentionDays": 365
}
```

| Key | Default | Meaning |
|---|---|---|
| `ApiListenUrls` | `http://127.0.0.1:5080` | `;`-separated listeners |
| `PublicOrigin` / `WebAuthnExpectedOrigin` | `http://localhost:5080` | Set to the real frontend origin in production |
| `WebAuthnRpId` | `localhost` | Passkey RP ID (must match the origin's domain) |
| `TrustedSetupNetworks` | loopback | CIDRs allowed to register the **first** passkey; closed after |
| `HeartbeatSilenceThresholdS` | 300 | Seeded agent-silent rule threshold |
| `RateLimits__GlobalPerMinute` / `PerSourcePerMinute` / `RegistrationPerMinute` / `AuthPerMinute` | 1200 / 300 / 20 / 30 | Protocol rate budgets |
| `AllowInsecureHttp` | false | **Dev only.** Never true in production |
| `VaultKeyPath` | `{data}/vault.key` | Override if the key must live elsewhere |
| `SQLiteBusyTimeoutMs` | 5000 | Busy timeout |

### 4.4 Run as a Windows service

```powershell
New-Service -Name hyveman-api `
  -BinaryPathName "C:\hyveman\api\hyveman-api.exe --data-dir C:\hyveman\data" `
  -StartupType Automatic -Description "Hyveman backend"
Start-Service hyveman-api
# or: sc.exe create hyveman-api binPath= "…" start= delayed-auto
```

The service account needs **read/write on the data directory only**. Verify:

```powershell
Invoke-RestMethod http://127.0.0.1:5080/health/live   # {"ok":true,…}
Invoke-RestMethod http://127.0.0.1:5080/health/ready  # {"ok":true}
# On Linux: run the same binary as a console process / systemd unit; --data-dir is the only required arg
```

### 4.5 First-run bootstrap: passkey + registration tokens

> **UI note:** these steps use the web UI, which is **not implemented yet**
> (§5). Until then the same endpoints can be driven through the API's
> OpenAPI page — but only in a **dev build**, where `/openapi/v1.json` is
> anonymous; in production it requires an authenticated session, so
> first-run setup must wait for hyveman-web. The console covers only passkey
> list/remove/reset, not first-time setup or token issuance.

1. **Passkey setup** (one time, from a network in `TrustedSetupNetworks`):
   open the web UI and register a passkey. With an empty `passkeys` table,
   setup is open only to trusted networks; after the first passkey it is
   closed. There is **deliberately no remote recovery** — keep at least two
   passkeys.
2. **Issue a registration token** for each host you will install:
   web UI → Sources → New registration token → kind `windows-agent`,
   lifetime (minutes). The UI shows the raw `reg_…` token **once** — copy it
   to the target host. Tokens are single-use (PROTOCOL §5).

Console fallbacks for passkey management:

```powershell
hyveman-api auth list-passkeys --data-dir C:\hyveman\data
hyveman-api auth remove-passkey <id> --data-dir C:\hyveman\data
hyveman-api auth reset --data-dir C:\hyveman\data
```

---

## 5. Install the frontend (hyveman-web)

> `hyveman-web` lives in this repository under `hyveman-web/` (React +
> TypeScript + Vite, per [`docs/FRONTEND.md`](docs/FRONTEND.md)). It is built
> into static files and deployed alongside the API; first-run passkey setup
> and registration-token creation go through it.

### 5.1 What it is

`hyveman-web` is a React + TypeScript SPA built by Vite into static files
(`dist/`). It is **not** served by the .NET process: nginx on the API VM
serves the files and reverse-proxies `/api/` to `hyveman-api`, so the
browser sees one HTTPS origin (`https://<fqdn>/`). The single origin is
what keeps the session cookie, CSRF origin checks, and WebAuthn passkey
ceremonies simple (FRONTEND.md §3, API.md §8.2).

### 5.2 Build

On any machine with a current Node.js LTS:

```bash
cd hyveman-web
npm ci
npm run lint && npm run typecheck && npm run test -- --run
npm run build          # → dist/
```

The build input is the pinned OpenAPI document in `hyveman-web/openapi/`;
CI must fail if the generated client diff is stale (`npm run api:check`,
FRONTEND.md §13).

### 5.3 Deploy the static files

Copy `dist/` onto the API VM. Keep versioned release directories and swap
a symlink so a rollback is a one-line change:

```bash
sudo mkdir -p /var/www/hyveman/releases
sudo rsync -a --delete dist/ /var/www/hyveman/releases/<build-id>/
sudo ln -sfn /var/www/hyveman/releases/<build-id> /var/www/hyveman/current
```

Rollback: point `current` at the previous release directory.

### 5.4 nginx site config + TLS

This repo ships the site config used on the API VM:

| File | Install to |
|---|---|
| `deploy/nginx/hyveman-linux-vm.conf` | `/etc/nginx/sites-available/hyveman` + symlink into `sites-enabled/` |

It serves the SPA from the configured `root` (this VM: `/home/user/www/hyveman/current`),
proxies `/api/`,
`/register`, `/ingest/`, `/health` to `http://127.0.0.1:5080`, and applies
security headers from FRONTEND.md §3/§13 and API.md
§12. Replace the `server_name` with your FQDN, then:

```bash
sudo nginx -t && sudo systemctl enable --now nginx
sudo apt install certbot python3-certbot-nginx
sudo certbot --nginx --redirect --hsts -d <fqdn>   # needs public port 80 for the HTTP-01 challenge
```

The API's `PublicOrigin` / `WebAuthnRpId` / `WebAuthnExpectedOrigin` must
match the resulting origin (§4.3), and `TrustedSetupNetworks` must include
the network the first passkey registration comes from.

### 5.5 Verify

| Check | How |
|---|---|
| Frontend serves | `curl -I https://<fqdn>/` → 200, `Cache-Control: no-cache` |
| API proxied | `curl https://<fqdn>/health/live` → `{"ok":true,…}` |
| SPA fallback | `curl -I https://<fqdn>/hosts` → 200 index.html, not 404 |
| First passkey | Browser → `https://<fqdn>/setup` from a trusted network; then `/login` |

---

## 6. Install the agent (hyveman-agent)

### 6.1 One-liner install

Copy `hyveman-agent.exe` (from `hyveman-agent\out\`) to the target host, then:

```powershell
.\install.ps1 -BackendUrl https://hyveman.example.com -InstallToken reg_<from-UI> [-EnableHyperV]
```

What it does (idempotent — re-running is safe):
1. Creates `C:\Program Files\hyveman-agent\` and `C:\ProgramData\hyveman-agent\`
   (config, spool, state, logs) with ACLs: **SYSTEM + Administrators only**.
2. Copies the exe and writes `agent.json` (snake_case, AGENT §10) with the
   backend URL and the one-time `reg_` token.
3. `-EnableHyperV`: enables the Hyper-V operational channels
   (`wevtutil set-log`) and adds the Hyper-V channel set to the config.
4. Registers the `HyvemanAgent` EventLog source (lifecycle events, IDs 1–5,
   which the agent self-collects with an allowlist to prevent recursion).
5. Creates the `hyveman-agent` service
   (`delayed-auto`, recovery: 3 restarts/5 s then **STOP** for 4 h).
6. Runs a **preflight** (TCP reachability to the backend + `--validate-config`)
   and fails closed if anything is wrong.
7. Starts the service.

On first start the agent exchanges the `reg_` token for a long-lived
`agt_` ingest token via `POST /register`, stores it in `agent.json`
(ACL'd), and **discards the reg token** (PROTOCOL §5.2).

### 6.2 Default channel set

| Config entry | Channel | Filter |
|---|---|---|
| `System` | System | Warning+ |
| `Application` | Application | Warning+ |
| `Security` | Security | curated IDs (4624 LT 2/10, 4625, 4740) via `security_log` |
| `HyvemanAgent` | Application | provider `HyvemanAgent`, IDs 1–5 (self-collect lifecycle) |

The Security channel subscription requires an account that can read it —
the default service account (LocalSystem) can. If you run the service under
a different account, grant it read on the Security log.

### 6.3 Verify an install

```powershell
Get-Service hyveman-agent                 # Running
Get-Content C:\ProgramData\hyveman-agent\logs\hyveman-agent-.log -Tail 20
```

Expect in the log, in order:
```
Registered as source src_… (scopes: ingest); ingest token stored in agent.json, reg token discarded
Backend health: ok=true (server 0.1.0); source_id=src_… scopes=[ingest]
Subscribed to channel System (query: *[System[Level<=3]])
APPLICATION STARTED — all hosted services up
```

Then confirm on the server: the host appears in the web UI (Sources/Hosts
— once hyveman-web exists, §5) with heartbeats (every 30 s by default) and
green health; event log records arrive as they occur.

### 6.4 Reinstall / move / forget a token

- **Reinstall, same host:** `install.ps1` preserves an existing valid
  `agent.json` (it holds the ingest token). To start fresh, delete
  `C:\ProgramData\hyveman-agent\agent.json` and re-run install with a new
  `reg_` token — the server **reuses the same `source_id`** because
  `(kind, hostname)` is authoritative (PROTOCOL §5.2).
- **`410 token_consumed`:** the reg token was already used (e.g. response
  lost mid-registration). Reissue a fresh reg token in the UI and restart.
- **Rotate a token:** revoke the `agt_` token in the web UI, delete
  `agent.json`, re-register with a fresh reg token. The agent keeps the
  spool file and retries slowly with `degraded="auth_rejected"` until then.
- **Uninstall:** `.\uninstall.ps1` (removes service, files, and any
  Hyper-V channels it enabled).

---

## 7. End-to-end verification checklist

| # | Check | Command / where |
|---|---|---|
| 1 | API alive | `GET /health/live`, `GET /health/ready` on the API |
| 2 | Agent protocol reachable | `GET /health` with `X-Hyveman-Protocol: 1` (no token) → `{"ok":true,…}` |
| 3 | Token resolves | `GET /health` with `Authorization: Bearer agt_…` → body contains `source_id` + `scopes` |
| 4 | Host registered | `sources` table: `SELECT * FROM sources;` |
| 5 | Heartbeats fresh | `agent_status.last_received` within the last interval; `degraded` empty |
| 6 | Logs ingesting | `events` row count grows after a real event occurs on the host |
| 7 | Idempotency | Replay a batch → response `"deduped"` counts, never duplicates |
| 8 | Web UI *(once hyveman-web exists; §5)* | Host visible, green health, alerts page empty |
| 9 | Cert validation | Agent log has **no** `TLS VALIDATION DISABLED` warning |

---

## 8. Day-2 operations

- **Backups:** back up the whole API data directory **including `vault.key`**
  — they are a unit (lost key = unrecoverable credentials). The API also
  writes daily `VACUUM INTO` snapshots into `{data}\backup\` (7 daily /
  4 weekly / 12 monthly) — copy that folder off-box.
- **Diagnostics:** the repo's `tools/` (see `tools/README.md`) ships two
  .NET 10 helpers that work against any server data dir:
  `tools/query-db.ps1 -DataDir {data-dir}` peeks at the SQLite DB (tables,
  sources, hosts, vms, alerts, events, audit, … or arbitrary SQL), and
  `tools/mint-reg-token.ps1 -DataDir {data-dir}` seeds a `reg_` token
  directly (test/staging only — production enrollment uses the web UI,
  §4.5). Build them with `dotnet build tools\dbquery` and
  `dotnet build tools\mint-reg-token` on a .NET 10 machine and copy the
  published folders, or run from a clone.
- **Retention:** `LogRetentionDays` (default 365) purges events/metrics/
  snapshots; maintenance runs daily.
- **Agent updates:** replace the exe (`Stop-Service`, copy, `Start-Service`);
  config is untouched.
- **Alerting:** notification channels (Telegram/webhook/SMTP) are configured
  in the web UI; alerts are evaluated server-side (health transitions, event
  matches, heartbeat silence, thresholds) with cooldown + dedup.
- **Hardware polling:** iDRAC/Redfish credentials enter via the admin API
  (vault-encrypted, never in config files).

---

## 9. Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| Agent exits at start: `Registration with backend failed` | Bad/expired `reg_` token, wrong `kind`, or unreachable backend. Check URL + firewall; reissue token if `410` |
| `410 token_consumed` on reinstall | Reuse of a spent reg token — issue a new one (server keeps the same source) |
| Agent log: `BACKEND TLS VALIDATION DISABLED` | `validate_cert=false` is set — remove it; pin `ca_path` instead |
| Agent log: cert/chain errors with `ca_path` | CA file is DER/PEM of the **issuer** (or the self-signed cert itself); agent does not use the system store when `ca_path` is set |
| Agent: `Backend health check failed` but API is up | Wrong `backend.url` (check scheme/host/port), proxy path mangling (agent paths must be exact), or cert hostname mismatch |
| Host visible but `degraded=spool_full` | Agent can't drain — disk free below `min_free_bytes` or backend down too long; check spool dir |
| Heartbeats stop, `agent-silent` alert | Agent service stopped/crashed (check recovery policy), network path broken, or token revoked |
| `429 too_many_requests` | Rate budget exceeded (per-source default 300/min); the agent honors `Retry-After` |
| Host shows wrong/duplicate name | v1 identifies by `(kind, hostname)`; rename the source in the UI |
| Event timestamps wrong | Host clock skew — fix NTP (server receive time drives alerting, not ingestion time) |
| API won't start: `readiness check failed` | SQLite locked/busy — check another process holding `hyveman.db`, or disk full |

---

## 10. References

- [`docs/PROTOCOL.md`](docs/PROTOCOL.md) — the wire contract (transport,
  auth, endpoints, error codes, retries). **Read before changing anything.**
- [`docs/AGENT.md`](docs/AGENT.md) — agent build contract, config reference
  (agent.json §10), spool/send semantics.
- [`docs/API.md`](docs/API.md) — backend design, options (§11), deployment.
- [`docs/DESIGN.md`](docs/DESIGN.md) — the overall system contract.
- Agent/API `README.md` files — build & test commands, repo layout.
