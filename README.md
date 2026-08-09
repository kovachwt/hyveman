# Hyveman

Windows Server log aggregator & hardware health monitor for a small fleet of
Dell PowerEdge (AMD EPYC) Hyper-V hosts. Three independently built and
deployed components: a Windows service **agent** that collects Event Logs and
Hyper-V WMI facts, a .NET **API** backend (ingest, alert engine, Redfish
hardware polling, SQLite storage), and a React **web** operations console.

```text
┌────────────────┐   outbound HTTPS only, no inbound listener   ┌──────────────┐
│      Agent     │ ── POST /register, /ingest/logs, ──────────► │  hyveman-api │
│ (server or VM) │    /ingest/telemetry, GET /health            │   (SQLite)   │
└────────────────┘    bearer token (agt_…), X-Hyveman-Protocol  └──────┬───────┘
                                                                       │ /api/v1
                                                                       ▼
                                                        hyveman-web (browser SPA)
```

| Component | What it is | Docs |
|---|---|---|
| [`hyveman-agent/`](hyveman-agent/) | Windows service (.NET 10, C#): event-log collection (`EvtSubscribe`, bookmarks), curated Security logon IDs, durable disk spool, heartbeat + Hyper-V WMI facts, HTTPS ingest with backoff/retry | [`docs/AGENT.md`](docs/AGENT.md) |
| [`hyveman-api/`](hyveman-api/) | Backend (.NET 10, ASP.NET Core): agent ingest, web/admin API, alert engine, Dell iDRAC Redfish poller, notification outbox (Telegram/webhook/SMTP), SQLite (WAL + FTS5), AES-GCM credential vault | [`docs/API.md`](docs/API.md) |
| [`hyveman-web/`](hyveman-web/) | Operations console (React 19 + TypeScript + Vite): fleet overview, event search, alerts/rules/channels, passkey-only login (WebAuthn) | [`docs/FRONTEND.md`](docs/FRONTEND.md) |

The agent↔server wire contract is fixed by
[`docs/PROTOCOL.md`](docs/PROTOCOL.md) (v1, with an embedded JSON schema at
[`docs/schemas/protocol-v1.json`](docs/schemas/protocol-v1.json)); the system
contract is [`docs/DESIGN.md`](docs/DESIGN.md). The agent protocol and the
web API are deliberately separate contracts.

## Repository layout

```text
docs/                 DESIGN, PROTOCOL, API, AGENT, FRONTEND + wire schema
hyveman-agent/        agent source (src/Hyveman.Agent), tests, build/install scripts
hyveman-api/          backend source (src/Hyveman.*), tests
hyveman-web/          React SPA, generated OpenAPI client, e2e tests
Hyveman.Api.sln       API + test solution (repo root)
Hyveman.Agent.sln     agent + test solution (repo root)
deploy/nginx/         single-file production nginx site for the API VM (TLS terminates at proxy)
DEV-STACK.md          run all three projects together on a dev machine (generic)
INSTALL.md            production install & operations guide
```

## Stack

| Piece | Technology |
|---|---|
| Agent | C# / .NET 10, Win32 Event Log API (`EvtSubscribe`), WMI (`root\virtualization\v2`), self-contained single-file exe |
| API | C# / .NET 10 / ASP.NET Core, SQLite (Microsoft.Data.Sqlite, WAL + FTS5), Serilog, WebAuthn (Fido2), Redfish polling |
| Web | React 19, TypeScript, Vite 7, MUI 7, TanStack Query, React Router 7, ECharts, `@simplewebauthn/browser`, Orval-generated API client, Vitest + Playwright |
| Protocol | HTTPS JSON, versioned (`X-Hyveman-Protocol: 1`), idempotent log batches, latest-wins telemetry, token auth (hashed `reg_`/`agt_`) |

## Quick start (local dev)

The full local 3-project stack (agent → API → web with real data end to end)
is documented in [`DEV-STACK.md`](DEV-STACK.md): prerequisites, self-signed
dev TLS, API/agent/web startup, first-run passkey setup, verification
checklist, and troubleshooting.

In short:

```bash
# 1. Build (needs .NET 10 SDK, Node 22 LTS)
dotnet build Hyveman.Api.sln
dotnet build Hyveman.Agent.sln
(cd hyveman-web && npm ci)

# 2. API with a data dir (creates DB, vault key, config on first start)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project hyveman-api/src/Hyveman.Api --no-launch-profile -- \
  --data-dir /path/to/devdata/api --WebAuthnExpectedOrigin http://localhost:5173

# 3. Web dev server (proxies /api to the API)
(cd hyveman-web && HYVEMAN_API_PROXY=https://127.0.0.1:8443 npm run dev)
#    → http://localhost:5173, register the admin passkey on the Setup page
```

## Production install

[`INSTALL.md`](INSTALL.md) covers building/publishing self-contained
binaries, TLS topology (reverse proxy vs. direct Kestrel), the agent
one-liner installer (`install.ps1`), first-run passkey bootstrap, nginx
deployment, day-2 operations (backups, retention), and troubleshooting.
Recommended topology: one public HTTPS origin — nginx serves the SPA and
reverse-proxies `/api/`, `/register`, `/ingest/`, `/health` to the API on
loopback.

## Build & test

```bash
dotnet build Hyveman.Api.sln
dotnet test  Hyveman.Api.sln        # protocol/contract/application/infrastructure/api suites
dotnet build Hyveman.Agent.sln
dotnet test  Hyveman.Agent.sln    # 97 unit/property tests
(cd hyveman-web && npm run lint && npm run typecheck && npm run test -- --run)
(cd hyveman-web && npm run build)               # static artifact in dist/
```

## Security highlights

- **Passkey-only** web authentication (WebAuthn/FIDO2) — no passwords; first
  setup only from trusted networks, console-only reset fallback, no remote
  recovery.
- Agent protocol is **outbound-only HTTPS** with revocable bearer tokens
  (hash-only storage); the server never initiates connections.
- Secrets (iDRAC credentials, notification tokens) live in an AES-GCM vault
  in the data directory; all state is in **one data directory** per server,
  so backup = copy the folder (daily `VACUUM INTO` snapshots, 7/4/12 ladder).
- Curated Security log forwarding only (4624 LT 2/10, 4625, 4740), aggregated
  server-side into per-user/per-day logon stats.
- The agent runs under resource containment (memory kill-cap, CPU hard-cap)
  and a fail-closed config; an invalid `agent.json` never starts the service.

## Status & roadmap

Phase 1 (MVP) is largely implemented across the three repos. See
[`docs/DESIGN.md`](docs/DESIGN.md) §10 for the roadmap; phases 2–3 add
Hyper-V depth, full rule/threshold engines, iDRAC SNMP/syslog receive,
in-guest agents, and non-Dell providers. The wire protocol already reserves
the future command channel (DESIGN §12).

---

Contract docs: [`docs/DESIGN.md`](docs/DESIGN.md) ·
[`docs/PROTOCOL.md`](docs/PROTOCOL.md) · [`docs/API.md`](docs/API.md) ·
[`docs/AGENT.md`](docs/AGENT.md) · [`docs/FRONTEND.md`](docs/FRONTEND.md).
Guides: [`DEV-STACK.md`](DEV-STACK.md) (dev) · [`INSTALL.md`](INSTALL.md) (prod).
