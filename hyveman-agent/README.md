# Hyveman

Windows Server log aggregator & hardware health monitor — **agent** implementation.

| Doc | Role |
|---|---|
| [`docs/DESIGN.md`](docs/DESIGN.md) | System design contract |
| [`docs/PROTOCOL.md`](docs/PROTOCOL.md) | Wire protocol spec (v1) — the network boundary |
| [`docs/AGENT.md`](docs/AGENT.md) | Agent build contract (this repo implements it) |

## What's here

- **`src/Hyveman.Agent/`** — `hyveman-agent`, the Windows service (C# / .NET 10,
  self-contained single-file exe):
  - `EvtSubscribe` push subscriptions per channel with persisted bookmarks
    (no gaps / no duplicates on the happy path, crash-safe via
    `(source_id, dedup_scope, record_id)` idempotency + epoch-prefixed
    `record_id` after a channel clear).
  - Durable bounded spool (`max_bytes` + `min_free_bytes` caps, drop-oldest,
    atomic writes), HTTPS ingest to `/ingest/logs` (gzip) and
    `/ingest/telemetry` (heartbeat + Hyper-V WMI facts, latest-wins).
  - Batch shaping: `batch_max_events` / `batch_max_age_ms` batching,
    `max_batch_bytes` chunking, `max_raw_bytes` truncation with an explicit
    `…hyveman-truncated:` marker.
  - Send-outcome machine in the sender: 2xx → delete; non-retryable 4xx /
    permanent per-item rejects → quarantine to `state\quarantine\`; 413 /
    `too_many_items` → split the batch in half and resend; credential-class
    4xx (`token_invalid`/`token_revoked`/`wrong_scope`/`unknown_source`) →
    keep the spool file (never quarantine a good batch for a bad token),
    surface `auth_rejected`, retry slowly so a rotated token self-heals;
    408/429/5xx/network → exponential backoff (1 s → 60 s, ±20 % jitter,
    honors `Retry-After`) — no retry storm while the backend is down.
  - Curated Security log (4624 LT2/10, 4625, 4740).
  - Job Object containment: process memory kill-cap (256 MiB), CPU rate
    hard-cap (25%), Below Normal priority — the agent cannot degrade the host.
  - WMI facts via `root\virtualization\v2` (serialized, timeout-bounded,
    stale-cached): VM list/state, heartbeat, CPU%, memory.
  - Heartbeats carry counters (events/batches sent & dropped, spool bytes /
    files, queue depth, WMI timeouts, send errors last min), `config_hash`,
    and a priority-ordered `degraded` flag (`spool_full`, `overrun`,
    `auth_rejected`, `quarantined`, `wmi_degraded`, `channel_reset` — sticky
    for 2 min so transient saturation still reaches the backend).
  - Config hot-reload of the safe subset (structural changes — URL/token/
    channels/caps — are logged and require a restart); `GET /health` /
    `POST /register` bootstrap (one-time `reg_` token → long-lived `agt_`
    token). A non-fatal startup health probe with token introspection runs
    once at boot; a down backend never blocks startup (spool + retry cover it).
  - Lifecycle events in the `HyvemanAgent` Application-log source (IDs 1–5:
    started/stopped/critical/preflight-fail/recovery-cap); a self-collect
    channel entry maps to Application + provider filter, allowlisted to
    prevent recursion.
  - TLS: pinned CA via `backend.ca_path` (custom-root trust chain) or
    `validate_cert=false` lab mode (logged loudly, never for production).
  - CLI: `--config`, `--data-dir`, `--validate-config`. Config is validated
    fail-closed at startup — an invalid `agent.json` never starts the service.
- **`tests/Hyveman.Agent.Tests/`** — unit/property tests (97 passing).
- **`tests/Hyveman.Agent.FaultHarness/`** — fault-injection tooling start
  (mock backend for backend-down/soak scenarios, AGENT §19.B #1/#3).
- **`tests/EvtSubscribeProbe/`** — small dev probe for the wevtapi layer.
- **`build.ps1`** — publish the single-file exe (AGENT §11.1).
- **`install.ps1`** — one-liner idempotent install (AGENT §11.2/§11.3):
  dirs + ACLs, config, Hyper-V channels (opt-in), EventLog source, SCM
  service with recovery (3 restarts / 4 h then STOP), preflight, start.
- **`uninstall.ps1`** — clean removal (AGENT §11.4).

## Build

```powershell
./build.ps1            # → out\hyveman-agent.exe (~68 MB self-contained)
```

## Install (per host)

```powershell
./install.ps1 -BackendUrl https://hyveman.example.lan:8443 `
              -InstallToken reg_<admin-issued> `
              [-EnableHyperV]
```

On first start the agent exchanges the `reg_` token for an ingest token
(`POST /register`, PROTOCOL §5) and persists it in `agent.json` (ACL'd).

## Verify a config without installing

```powershell
hyveman-agent.exe --config C:\ProgramData\hyveman-agent\agent.json --validate-config
# --data-dir C:\path overrides the data directory (config default: C:\ProgramData\hyveman-agent)
```

## Layout

```
C:\ProgramData\hyveman-agent\
  agent.json            config (SYSTEM + Administrators only)
  spool\                durable log batches (capped; drop-oldest)
  state\                bookmarks, epochs, quarantine\, pid lock
  logs\                 agent's own rolling logs (10MB x 5)
```

## Development notes (lessons from the smoke test)

- `services.AddHostedService(factory)` uses `TryAddEnumerable`, which
  **deduplicates factory registrations by return type** — multiple
  subscribers registered this way collapse to one. Use
  `AddSingleton<IHostedService>(...)` for N-of-a-kind services.
- Never block inside an async `ExecuteAsync` (a `Monitor.Wait` on the
  startup thread stalls the host's sequential service start).
- `EnumerateInstances(ns, queryString)` treats the string as a class *name*;
  WQL must go through `QueryInstances(ns, "WQL", ...)`.
- `EvtRender(EVT_RENDER_EVENT_VALUES)` requires a rendering context from
  `EvtCreateRenderContext(NULL paths, EvtRenderContextSystem)` — passing
  NULL context fails with `ERROR_INVALID_PARAMETER` (87).
- `EvtRender(EVT_RENDER_BOOKMARK)` needs a bookmark handle
  (`EvtCreateBookmark` + `EvtUpdateBookmark`), not an event handle.
- The size-probe call (`BufferSize=0, Buffer=NULL`) failing with
  `ERROR_INSUFFICIENT_BUFFER` is the normal two-step contract.
