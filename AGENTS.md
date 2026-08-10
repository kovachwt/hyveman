# AGENTS.md

## Project

Hyveman: Windows Server log aggregator & hardware health monitor for Dell PowerEdge Hyper-V hosts. Three independently built/deployed components:

- `hyveman-agent/` — Windows service (.NET 10, C#): Event Log + Hyper-V WMI collection, HTTPS ingest
- `hyveman-api/` — Backend (.NET 10, ASP.NET Core): ingest, alert engine, Redfish polling, SQLite
- `hyveman-web/` — React 19 + TypeScript + Vite SPA: operations console, passkey-only (WebAuthn) login

## Docs first

Contracts live in `docs/` — read before changing behavior:

- `docs/DESIGN.md` — system contract, roadmap
- `docs/PROTOCOL.md` + `docs/schemas/protocol-v1.json` — agent↔server wire protocol (v1, **fixed**; changing it requires versioning)
- `docs/API.md`, `docs/AGENT.md`, `docs/FRONTEND.md` — per-component behavior
- `DEV-STACK.md` (local dev), `INSTALL.md` (production)

The wire protocol and web API are deliberately separate contracts. Don't couple them.

## Commands

```bash
# API (.NET 10)
dotnet build Hyveman.Api.sln
dotnet test  Hyveman.Api.sln

# Linux server only — rebuild API, sync run/api/, restart the user unit. No-op on Windows. Run when finished with server changes
[ "$(uname -s)" = Linux ] && [ -f ~/.config/systemd/user/hyveman-api.service ] && dotnet build Hyveman.Api.sln && cp -a hyveman-api/src/Hyveman.Api/bin/Debug/net10.0/. run/api/ && systemctl --user restart hyveman-api 

# Linux server only — build web SPA, deploy a versioned release dir + atomic symlink swap (nginx serves it). Run when finished with web changes
[ "$(uname -s)" = Linux ] && (cd hyveman-web && npm ci && npm run build) && REL="$HOME/www/hyveman/releases/$(date -u +%Y%m%d-%H%M%S)" && mkdir -p "$REL" && cp -a hyveman-web/dist/. "$REL/" && ln -sfn "$REL" "$HOME/www/hyveman/current"

# Agent (.NET 10)
dotnet build Hyveman.Agent.sln
dotnet test  Hyveman.Agent.sln

# Web (Node 22)
(cd hyveman-web && npm ci)
(cd hyveman-web && npm run lint && npm run typecheck && npm run test -- --run)
(cd hyveman-web && npm run build)

# Ops tools (see tools/README.md)
./tools/query-db.ps1                        # peek at a server DB (dev fallback, or -DataDir)
./tools/mint-reg-token.ps1 -DataDir <dir>   # seed a reg_ token (dev/test only)
```

## Conventions

- All state lives in one data dir per server (SQLite + config + AES-GCM vault); backup = copy the folder.
- Agent traffic is outbound-only HTTPS, bearer-token auth; server never initiates connections.
- Web auth is passkey-only — no passwords.
- Tests live alongside each component (`hyveman-api/tests/`, `hyveman-agent/tests/`, `hyveman-web/e2e`).
