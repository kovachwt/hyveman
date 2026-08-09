# Hyveman Agent — Technical Design Document

Build contract for `hyveman-agent`, the Windows service that runs on each
host (Hyper-V hosts and optionally guest VMs). This document is the
implementation-level counterpart to `docs/DESIGN.md` (which is the system
design contract). It focuses on the agent; it defers the wire-protocol
*contract* to the wire-protocol spec (DESIGN §14 #1) and the full config
*reference* to the agent-config spec (DESIGN §14 #2), while describing what the
agent emits and consumes.

- **Language/runtime:** C# / .NET 10 (LTS), self-contained single-file exe.
- **Target OS:** Windows Server 2019+ (and Windows 10/11 for lab).
- **Form:** Windows service, single process, no child processes.
- **Scope (MVP):** event log tail + curated Security + heartbeat + durable
  spool + HTTPS ingest + Hyper-V facts via WMI. In-guest deployment uses the
  same binary (DESIGN §2.3).

---

## 1. Goals & non-goals

### 1.1 Goals
1. Collect Windows Event Logs from configured channels with **no gaps and no
   duplicates on the happy path**, surviving agent restart, backend outage,
   and channel clear-wrap.
2. Stay inside a hard resource envelope so the agent can **never degrade the
   host** (a Hyper-V host running up to ~50 VMs) — see §3.
3. Be a **well-behaved guest** in the WMI namespace Hyper-V and Failover
   Clustering also use — never stall or starve them (see §4.4, §3).
4. Report Hyper-V inventory + per-VM heartbeat/CPU/RAM as structured facts.
5. Heartbeat with self-status so the backend can raise an "agent silent"
   alert and graph degradation (dropped events, spool saturation, WMI timeouts).
6. ~10 s install, single-file exe, one config file, idempotent reinstall.
7. Crash-safe at-least-once delivery; the backend's idempotency key collapses
   replays (DESIGN §4.3, §13 #11).

### 1.2 Non-goals (for the agent)
- No inbound network listener. The agent is **strictly client-only**
  (DESIGN §12 #1). No port opens, ever.
- No enabling/modifying of event channels at runtime (DESIGN §4.1 —
  installer-only; held as a **testable invariant**, §15.5).
- No registry writes at runtime.
- No command execution (the command channel is deferred per DESIGN §12; the
  protocol is versioned now so it can be added without rework).
- No persisting of secrets other than the agent's own ingest token.

---

## 2. Requirements traceability

| DESIGN ref | Requirement | Where addressed here |
|---|---|---|
| §4.1 | `EvtSubscribe` per channel, persisted bookmark, batched HTTPS, local spool (bounded, drop oldest with metric) | §6, §7, §8 |
| §4.1 | Default channels incl. Hyper-V (installer-enabled), iSM/iDRAC, cluster, storage | §11.2, App. B |
| §4.1 | Curated Security (4624 LT2/10, 4625, 4740) + per-user/day aggregation (server-side) | §6.4 |
| §4.1 | Min level + per-channel include/exclude IDs at source | §6.2, §10 |
| §4.1 | Hyper-V WMI `root\virtualization\v2`: VM list, state, heartbeat, CPU/RAM as facts | §7 |
| §4.1 | Heartbeat: version, OS build, uptime, free disk | §8 |
| §4.1 | Per-agent token + TLS | §13 |
| §4.1 | Single self-contained exe, ~10 s install, local or pushed config | §11 |
| §11 | Generic envelope, `record_id` = EventRecordID (epoch-prefixed on clear, DESIGN §13 #15), `dedup_scope` = channel | App. A |
| §12 | Versioned protocol, scope-capable tokens (ingest-only today) | §13, §9 |
| §13 #11 | `(source_id, dedup_scope, record_id)` idempotency, non-null dedup_scope | §6.6, §8 |
| Safety (this doc) | Never degrade host; safe-failure modes | §3, §4, §16 |

---

## 3. Threat model & safety envelope

A user-mode Windows service **cannot BSOD the host.** The realistic harms are
all *resource* harms, and the worst of them is the agent not crashing but
silently degrading a shared resource. The host's OS volume and the
`root\virtualization\v2` WMI namespace are the two shared resources we must
not abuse.

| # | Harm | Mechanism that prevents it | Where |
|---|---|---|---|
| H1 | **Disk fill** of OS/spool volume → VMs pause, live-migration fails, host wedges | Spool hard cap (bytes **and** min-free-bytes floor); drop-oldest + degraded flag; never write outside the spool dir | §4.1, §8.3 |
| H2 | **WMI stall** of `root\virtualization\v2` affecting Hyper-V/cluster operations | Per-query `OperationTimeout`, serialized scans, cached results, low cadence, async `CimSession` | §4.4, §7 |
| H3 | **EventLog service** stalled by a subscriber that won't drain | EvtSubscribe callback must not block → hand off to bounded in-memory channel; backpressure = drop, not block | §6.3 |
| H4 | **Memory leak** → slow exhaustion | Job Object process-memory cap → **OS kills the agent** on exceed; SCM recovery bounded | §4.2 |
| H5 | **CPU spin** (retry storm, event firehose, runaway loop) → host starvation | Job Object CPU rate cap; exp backoff + jitter + bounded send concurrency; self-limit on event flood | §4.2, §6.5, §8.3 |
| H6 | **Install side effects** (audit policy, channel, registry changes at runtime) | Channel changes installer-only; runtime writes confined to state dir; no registry writes | §11, §15.5 |
| H7 | **Crash loop** thrashing the host | SCM `RecoveryAction` capped (3 restarts within a 4 h window, then **stop**); "agent silent" alert fires | §4.3, §15 |

**Design principle:** every failure has a *safe* landing. If the agent can't
operate within its envelope, it **kills itself** (memory cap) or **stops
itself** (recovery cap) and raises "agent silent." A clean stopped agent is
strictly better than a running agent that is silently harming the host.

---

## 4. Runtime containment (the "never affects host" layer)

These mechanisms are what make "never" plausible. Most are testable (§19).

### 4.1 Spool — the controlled disk buffer
- One directory: `C:\ProgramData\hyveman-agent\spool` (configurable).
- **Two caps, both enforced before every write:**
  1. `spool.max_bytes` (default **100 MiB**) — absolute.
  2. `spool.min_free_bytes` (default **5 GiB**) — the agent must never let its
     spool writes cause the *volume's free space* to drop below this. If a
     write would cross it, the write is rejected (drop-oldest, then retry the
     new batch against the same checks).
- **On saturation:** delete oldest spool files until under cap, increment
  `events_dropped` and set `degraded = spool_full` in the next heartbeat.
  Never block the EvtSubscribe thread on spool I/O (§6.3).
- **Files only in this directory.** Bookmark/state files live in sibling
  `state\`, not on the OS volume's root or anywhere else.

### 4.2 Job Object — the OS-enforced kill switch (H4, H5)
The agent places itself in a Win32 Job Object at startup (PInvoke
`CreateJobObject`/`SetInformationJobObject`/`AssignJobObjectProcess`):

- **Process memory cap** (`JOB_OBJECT_LIMIT_PROCESS_MEMORY`): default
  **256 MiB** (`limits.process_memory_bytes`). Exceeding it → the OS kills the
  process. This is the one mechanism .NET cannot self-enforce mid-allocation,
  so it is load-bearing for "never." SCM then restarts; repeated kills hit the
  recovery cap (§4.3) and the agent stops itself cleanly → "agent silent."
- **CPU rate cap** (`JobObjectCpuRateControlInformation`, Win8+): default
  **25 % of a single logical processor** (`limits.cpu_rate_percent`). A
  runaway loop cannot starve the host. Normal operation needs far less.
- Both are configurable; both default to "safe for a Hyper-V host."

### 4.3 Service config (set by `install.ps1`, §11)
```
# Create via New-Service, NOT `sc create`: the binPath value embeds quotes
# around a path with spaces, and PowerShell 5.1 does not escape embedded quotes
# in native command lines — sc.exe would receive `binPath= C:\Program ...`
# (broken path, or exit 1639 ERROR_INVALID_COMMANDLINE on Server builds).
# New-Service / Win32_Service.Change pass the string to the SCM API verbatim.
New-Service hyveman-agent -BinaryPathName '"C:\Program Files\hyveman-agent\hyveman-agent.exe" --service' -StartupType Automatic
  # (PS 5.1 New-Service only accepts ServiceStartMode; delayed-auto set below)
sc config hyveman-agent start= delayed-auto
sc description hyveman-agent "Hyveman log & health agent"
sc failure hyveman-agent reset= 14400 actions= restart/5000/restart/5000/restart/5000
  # 3 restarts within 4 h then STOP (no infinite loop). Trigger backend "agent silent".
```
- `DelayedAutoStart` — don't compete with boot-critical services.
- Process priority **Below Normal** (set via Job Object `PriorityClass`).
- Service account: **`LocalSystem`** (default for now — DESIGN §13 #17). A
  dedicated least-privilege local account (member of *Event Log Readers*)
  is a documented **later option** the installer keeps open via a future
  `-Account` flag; not used now to avoid per-host account provisioning on
  the small fleet. `LocalSystem` already covers Security-log read and WMI.

### 4.4 WMI citizenship (H2) — be a guest in Hyper-V's namespace
`root\virtualization\v2` is the **same namespace** Hyper-V VMMS and Failover
Clustering use. Rules (enforced in code, §7):
1. **Single serialized scanner** — never run two concurrent WMI scans.
2. **Every query has an `OperationTimeout`** (default 20 s via
   `CimSessionOptions.Timeout`). A hung provider releases in seconds, never
   minutes.
3. **Cache the previous scan**; report it with `stale=true` if the current
   scan times out, so the backend never loses VM visibility abruptly.
4. **Low cadence** (default 60 s) — `wmi.scan_interval_s`.
5. **Async `CimSession`** (`Microsoft.Management.Infrastructure`), not the
   legacy `System.Management`, and never hold an enumeration open across a
   network send.

---

## 5. Process architecture

Single process, .NET generic host (`Microsoft.Extensions.Hosting` +
`Microsoft.Extensions.Hosting.WindowsServices`). Several long-lived
`IHostedService` / `BackgroundService` components coordinate over a bounded
in-memory channel and the on-disk spool.

```
┌──────────────────────── agent process (one .exe, one Job Object) ───────────────────────┐
│                                                                                          │
│  EvtSubscribePerChannel  ── N channels ── push callbacks ──►  Bounded in-mem Channel     │
│  (wevtapi.dll PInvoke)                                          <LogEvent>                 │
│                                                                  (cap: 10k; full=drop      │
│                                                                   oldest + metric)        │
│                                                                          │ ▲              │
│  BookmarkManager ◄── advances per channel after spool ack ──────────────┘ │              │
│                                                                          ▼                │
│                                                                   BatchBuilder             │
│                                                                   (≤500 evts or ≤1s)        │
│                                                                          │                │
│                                                                          ▼                │
│                                                                   SpoolWriter              │
│                                                                   (atomic temp+rename,     │
│                                                                    cap-checked)             │
│                                                                          │                │
│      ┌───────────────────────────────────────────────────────────────────┘                │
│      ▼                                                                                    │
│  SpoolDirectory  ── drained by ──►  LogSender ── HTTPS POST /ingest/logs ──► backend       │
│  (<ts>-<seq>.json)                 (concurrency=2,                        auth: Bearer    │
│                                     timeout=30s,                           token           │
│                                     exp backoff+jitter)                                    │
│      ▲ delete on 2xx                                                                       │
│                                                                                          │
│  WmiFactCollector ──► FactEnvelope ──► TelemetrySender ── HTTPS POST /ingest/telemetry ──►│
│  (serialized, 60s)       (in-memory)   (best-effort, 3 tries)                              │
│                                                                                          │
│  HeartbeatTimer ──► HeartbeatEnvelope ──► TelemetrySender ──► (same endpoint)             │
│  (30s)                                                                                     │
│                                                                                          │
│  RuntimeMonitor: in-mem queue depth, spool bytes, dropped/wmi-timeout counters ───────────┤
│  ConfigReload: optional file-watch (validates, hot-applies safe subset only)               │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

### 5.1 Component responsibilities
- **ChannelSubscriber (one per channel):** owns `EvtSubscribe` push
  subscription + the channel's bookmark. Callback renders the event and
  enqueues to the in-memory channel; never blocks.
- **BookmarkManager:** reads/writes per-channel bookmark files atomically
  (temp+rename). Advances a channel's bookmark only after a batch containing
  that channel's events is **durably spooled** (§6.6).
- **BatchBuilder:** drains the in-memory channel, forms batches bounded by
  `batch_max_events` / `batch_max_age_ms`, preserving per-channel order.
- **SpoolWriter:** writes each batch as one file under the spool dir,
  atomically (write `.tmp`, `fsync`, rename). Enforces both spool caps first.
- **LogSender:** drains spool files oldest-first; POSTs each as one batch;
  deletes on 2xx; retries with bounded exponential backoff + jitter; bounded
  concurrency (`limits.send_concurrency`).
- **WmiFactCollector:** serialized, timeout-bounded WMI scans → facts envelope.
- **HeartbeatTimer:** periodic heartbeat with runtime counters.
- **RuntimeMonitor:** single source of truth for the counters in heartbeats.
- **TelemetrySender:** best-effort sender for facts + heartbeat (not spooled).

---

## 6. Event log pipeline (detailed)

### 6.1 The wevtapi.dll layer
`System.Diagnostics.EventLog` exposes only the classic/read APIs, **not** the
push subscription. The agent implements a thin PInvoke layer over
`wevtapi.dll`:

- `EvtSubscribe` — push subscription via `SUBSCRIBE_CALLBACK` (or signal-based
  with `EvtSubscribePushAsync`); use `EvtSubscribeStartAfterBookmark`.
- `EvtRender(EVT_RENDER_EVENT_VALUES)` — get properties; `EvtFormatMessage(
  EVT_FORMAT_MESSAGE_MESSAGE/XML)` — rendered message + raw XML.
- `EvtCreateBookmark` / `EvtUpdateBookmark` — bookmark handle.
- `EvtRender(EVT_RENDER_BOOKMARK)` — serialize bookmark for disk.
- `EvtClose` — release handles (no leaks; tracked in RuntimeMonitor).

This PInvoke layer is a **first-class build deliverable** with unit tests,
including handle-leak property tests (§19.A).

### 6.2 Subscription + source-side filtering
- Per channel, build an XPath query that pushes level + event-ID filtering into
  the Event Log API (cheapest path):
  `*[System[(Level<=N) and (EventID=X or EventID=Y or ...)]]`
- Exclude IDs via the same XPath (`and (EventID!=Z and ...)`).
- Min level default **Warning** (`level=3`) for System/Application; per-channel
  override in config.
- **EventData-field predicates** (e.g. Security 4624 `LogonType`) are **not**
  expressed in XPath** — they're finicky in the structured-query subset. The
  agent subscribes to the curated event-ID set and applies the `LogonType`
  filter **in-process** for 4624 (§6.4). Logon volume is low, so this is cheap
  and far more maintainable.
- A configured channel that **does not exist** on the host is logged at
  warning and **skipped** (never crashes startup). This covers iSM/iDRAC channel
  absence on non-Dell boxes and Hyper-V channel absence in guest-VM installs.

### 6.3 Callback contract — never block the EventLog thread
The `SUBSCRIBE_CALLBACK` runs on an ETW thread-pool thread. It **must return
quickly**:
1. Render the event (cheap, local).
2. `channel.Writer.TryWrite(ev)` — non-blocking; on full, drop oldest event,
   increment `events_dropped`, set `degraded=overrun`. **Never block** here.
3. Return.

Backpressure never propagates to the EventLog service; it materializes as the
bounded drop policy + the degraded flag the backend alerts on. This is H3.

### 6.4 Curated Security log (per DESIGN §4.1, §13 #5)
- Channel `Security`, subscribe to event IDs `{4624, 4625, 4740}`.
- **In-process post-filter on 4624:** keep only `LogonType ∈ {2, 10}`
  (`security_log.logon_types_for_4624`). Drop other 4624s silently (not sent,
  not counted as errors). 4625/4740 pass through.
- Renders to the generic envelope like any other event; the backend does the
  per-user/day aggregation (DESIGN §5).
- **Audit policy requirement** ("Audit Logon Events") is an installer
  preflight check (§11.3) — documented, not silently enabled by the agent.

### 6.5 Batch + send (logs)
- `BatchBuilder` flushes at `batch_max_events=500` or `batch_max_age_ms=1000`.
- `SpoolWriter` writes the batch file **atomically** (`.tmp` → `fsync` →
  rename to `<spool>\<unixms>-<hexseq>.json`), after passing both spool caps.
- `LogSender` POSTs each spool file as one request to `/ingest/logs` with
  `Authorization: Bearer <token>`, `Content-Type: application/json`,
  `X-Hyveman-Protocol: 1`, body = the batch envelope (§9).
- **Retry policy:** exponential backoff (base 1 s, factor 2, per-attempt cap
  60 s) + ±20 % jitter. Retries are **unbounded** (per-attempt delay is
  capped; the spool honors its own caps, so a permanently-down backend can
  neither spin the host nor fill the disk — the spool keeps events safe; the
  sender just keeps them queued). **No retry storm**: bounded
  `send_concurrency=2`; while the backend is down the sender sleeps on the
  backoff, CPU ~0 (H5).
- **Response handling:** 2xx → delete spool file. 4xx (except 408/429) → the
  batch is malformed/unauthorized; quarantine to `state\quarantine\` (do **not**
  retry forever — that would loop), log loudly, surface in heartbeat.
  **Credential-class 4xx** (`401 token_invalid/token_revoked`,
  `403 wrong_scope`, `404 unknown_source`, `410 token_consumed`) is the one
  exception: the batch is valid, so it is **kept in the spool** (never
  quarantined), `degraded = auth_rejected` is set, and the file is retried
  slowly (5 min) so a rotated or re-registered token recovers it. 5xx /
  408 / 429 / network error → keep spool file, retry per backoff.

### 6.6 Bookmark lifecycle (the no-gap/no-dup invariant)
**Ordering rule (load-bearing):** for a given channel, the bookmark is
advanced **only after** a batch containing that channel's events is durably
spooled (file renamed to its final name). Consequence:

- Crash *after spool, before bookmark advance* → on restart, EvtSubscribe
  resumes from the pre-batch bookmark → re-reads the batch's events → the spool
  file already exists and is also sent → backend **dedup collapses** the
  replay via `(source_id, dedup_scope=channel, record_id)`. **No loss, dup-free.**
- Crash *before spool* → bookmark not advanced → events re-read and re-sent.
  No loss.
- Crash *after bookmark advance, before send* → events are in the spool,
  sent on restart. No loss.

Bookmark file: `state\<channel-safe>.bookmark` containing the serialized
bookmark XML + the `EventRecordID` and a monotonic `seq` for sanity. Atomic
(temp + rename). On startup: read → `EvtCreateBookmark` →
`EvtSubscribe(StartAfterBookmark)`.

### 6.7 Channel clear / EventRecordID wrap (idempotency edge case — see §21 #1)
`EventRecordID` is monotonic within a channel **but resets to 1 on a channel
clear**. After a clear, a new event gets RecordID `1`, colliding in the
backend's `UNIQUE(source_id, dedup_scope, record_id)` with the pre-clear event
that also had RecordID `1` — **the new event would be (wrongly) deduped away.**

**Fix (fits the existing TEXT contract; no backend schema change):**
`record_id` is opaque TEXT (DESIGN §13 #11), so the agent constructs it as:
- normal (default epoch 0): `record_id = "<EventRecordID>"`  (bare).
- after a detected reset: `record_id = "e<epoch>:<EventRecordID>"`, where
  `epoch` increments on each reset.

The reset is **detected** in two ways:
1. `EvtSubscribe(StartAfterBookmark)` returns a stale/invalid bookmark error
   → assume clear/wrap → resubscribe from "now" (`EvtSubscribeToFutureEvents`),
   bump epoch, emit a "channel_reset" synthetic event, set `degraded`.
2. The subscriber observes a RecordID **lower than the channel's persisted
   max** → same handling.

Because the backend dedups on the full TEXT string, `"1"` and `"e1:1"` are
distinct → both stored, replays within each epoch still collapse. This
refines the record_id contract; **ratified in DESIGN §13 #15** (no backend
schema change; the agent constructs `record_id` per the scheme above).

---

## 7. Hyper-V facts collector (WMI)

Per §4.4 rules. All queries against `root\virtualization\v2` via
`Microsoft.Management.Infrastructure` (CimSession with `Timeout`), one
serialized scan per `wmi.scan_interval_s` (default 60 s).

- **VM list + state:** `SELECT * FROM Msvm_ComputerSystem` (filter
  `Caption='Virtual Machine'`); map `OperationalStatus`/`EnabledState` →
  `{on,off,paused,saved,other,unknown}`.
- **Heartbeat:** per-VM Integration Services heartbeat from
  `OperationalStatus`/`Msvm_HeartbeatIntegrationService`-style status →
  `{ok,error,no_contact,unknown, stale}`.
- **Per-VM CPU/RAM/disk:** `Msvm_SummaryInformation` via
  `Msvm_VirtualSystemManagementService.GetSummaryInformation` (CPU utilization,
  memory assigned/used). Exact WQL is finalized in a **Hyper-V WMI reference
  sub-doc** (companion to the Redfish mapping table, DESIGN §14 #3) tied to the
  Server 2019 / Hyper-V version API surface.
- **VM name:** `vms[].name` = `ElementName` (request ID 1, the friendly display
  name), falling back to `Name` (request ID 0, the VM GUID) when empty — the
  GUID is never sent as the display name.
- **Output:** a single `facts` envelope per scan, sent best-effort via
  TelemetrySender. If a query times out, the prior facts are re-sent with
  `stale=true` and `wmi_timeouts++`; a "wmi_degraded" hint appears in the
  heartbeat so the backend can alert.

---

## 8. Heartbeat & self-reporting

Sent every `heartbeat.interval_s` (default **30 s**) via TelemetrySender
(best-effort, 3 quick retries, never spooled — a missed heartbeat *is* the
signal the backend alerts on, so replaying old ones is wrong).

Envelope fields:
| Field | Source |
|---|---|
| `agent_version` | assembly version |
| `protocol_version` | 1 |
| `os_build` | `Environment.OSVersion` / WMI `Win32_OperatingSystem` |
| `boot_time`, `uptime_s` | `GetTickCount64` / WMI |
| `free_disk` | `DriveInfo` for OS volume + spool volume (bytes + pct) |
| `source_id` | from registration (corroborates token-derived identity) |
| `counters` | `events_sent`, `events_dropped`, `batches_sent`, `batches_failed`, `spool_bytes`, `spool_files`, `queue_depth`, `wmi_timeouts`, `send_errors_last_min` |
| `degraded` | `""` \| `spool_full` \| `overrun` \| `auth_rejected` \| `quarantined` \| `wmi_degraded` \| `channel_reset` |
| `config_hash` | short hash of active config (change → backend notices) |

`degraded` is the backpack for everything in §3/H1–H3 and §16. The backend
graph + alert rules consume these (DESIGN §4.4 rule type 3 = heartbeat).

---

## 9. Wire protocol (what the agent emits)

The *contract* is the wire-protocol spec (DESIGN §14 #1). This section
fixes what the agent produces so builders have a target. **Two endpoints,
decided (DESIGN §13 #16):** semantically clean — `/ingest/logs` (idempotent,
agent-spooled) and `/ingest/telemetry` (latest-wins).

### 9.1 auth + headers
```
Authorization: Bearer <agent_token>
Content-Type: application/json; charset=utf-8
X-Hyveman-Protocol: 1
X-Hyveman-Source: <source_id>     # corroborating; authoritative identity = token
Content-Encoding: identity         # or gzip when enabled (§9.4)
```
**`source` is authoritative from the token.** The backend MUST ignore the
body's `source` field for identity (a compromised agent can't claim another
source). The body field is corroborating only.

### 9.2 log batch → `POST /ingest/logs`
```jsonc
{ "v": 1,
  "items": [
    { "kind":"log",
      "record_id":"41235",          // §6.7: bare, or "e<epoch>:<id>"
      "dedup_scope":"System",        // = channel name (DESIGN §13 #11)
      "time":"2024-08-07T15:02:11.123Z",  // UTC, event TimeCreated
      "severity":3,                  // Windows Level (1=Critical..5=Verbose)
      "facility":"Microsoft-Windows-Kernel-Power", // = provider name (DESIGN §5.1)
      "message":"The system ...",     // EvtFormatMessage MESSAGE
      "fields": {
        "channel":"System","event_id":6008,"task":0,"opcode":0,
        "keywords":"0x80000000000000","provider_guid":"{...}",
        "computer":"HOST01","activity_id":"...","process_id":0,"thread_id":0,
        "event_data":{"LogonType":10,"TargetUserName":"admin", ...}  // parsed EventData
      },
      "raw":"<Event xmlns='...'>...</Event>"  // EvtFormatMessage XML; capped (§9.3)
    }
  ]
}
```
The `fields` JSON holds the Windows-specific columns the backend promotes to
indexed columns (DESIGN §5.1): `channel`, `event_id`, `task`, `opcode`,
`keywords`, plus parsed `event_data`.

### 9.3 size guards
- `raw` capped at `limits.max_raw_bytes` (default **8 KiB**); over-cap →
  truncated with marker `"…hyveman-truncated:8192"` (stored, not dropped — the
  rendered `message` is still complete).
- Batch HTTP body capped at `limits.max_batch_bytes` (default **4 MiB**);
  larger → split.
- Individual event JSON capped; an over-large single event is still sent (with
  truncated `raw`), never silently dropped.

### 9.4 telemetry → `POST /ingest/telemetry`
Two item kinds in one body (or a single `kind:heartbeat` / `kind:facts`):
```jsonc
{ "v":1, "items":[
  { "kind":"heartbeat", /* §8 fields */ },
  { "kind":"facts", "vms":[ {name,state,heartbeat_ok,cpu_pct,mem_mb,...} ], "stale":false }
]}
```
Facts/heartbeat are **not idempotent by record_id** (latest-wins on backend);
not spooled.

### 9.5 response & idempotency semantics
- 2xx → logs: delete spool file. telemetry: discard.
- 4xx (non-retryable) → logs: quarantine + alert. telemetry: discard (will
  resend next interval anyway).
- 5xx / 408 / 429 / network → logs: keep spool file, retry per §6.5. telemetry:
  discard (next tick resends).
- Dedup is **backend-side** via `(source_id, dedup_scope, record_id)`; the
  agent assumes replays are safe and does not track "sent-but-unacked" beyond
  the spool file's existence.

### 9.6 gzip
Optional request body gzip for batches (`Content-Encoding: gzip`); enabled by
`limits.gzip: true` (default on for logs). Saves bandwidth at the cost of CPU
(inside the job cap).

---

## 10. Configuration

Single file: `C:\ProgramData\hyveman-agent\agent.json`. Bound to a strongly
typed `AgentOptions` via `Microsoft.Extensions.Configuration` with
**startup validation** (`ValidateOnStart`) — invalid config → service fails to
start with a clear event-log message (never starts in a half-broken state).

```jsonc
{
  "backend": {
    "url": "https://hyveman.example.lan:8443",   // base URL, no trailing slash
    "token": "agt_...",                          // ingest-scope bearer token (§13)
    "ca_path": null,                             // optional pinned CA for lab; null=system store
    "validate_cert": true                        // false = skip (DISCOURAGED; lab only)
  },
  "spool": {
    "dir": "C:\\ProgramData\\hyveman-agent\\spool",
    "max_bytes": 104857600,          // 100 MiB absolute cap (H1)
    "min_free_bytes": 5368709120     // never push volume free below 5 GiB (H1)
  },
  "limits": {
    "process_memory_bytes": 268435456,  // 256 MiB Job-Object cap → kill on exceed (H4)
    "cpu_rate_percent": 25,              // Job-Object CPU rate cap (H5)
    "in_memory_queue_events": 10000,     // bounded channel size (H3)
    "batch_max_events": 500,
    "batch_max_age_ms": 1000,
    "max_batch_bytes": 4194304,
    "max_raw_bytes": 8192,
    "send_concurrency": 2,
    "send_timeout_ms": 30000,
    "gzip": true
  },
  "wmi": {
    "scan_interval_s": 60,
    "query_timeout_s": 20,
    "max_queries_per_scan": 8
  },
  "heartbeat": { "interval_s": 30 },
  "security_log": {
    "enabled": true,
    "include_ids": [4624, 4625, 4740],
    "logon_types_for_4624": [2, 10]
  },
  "channels": [
    // App. B default set; installer writes the production set.
    {"name":"System","level":"Warning","include_ids":null,"exclude_ids":null},
    {"name":"Application","level":"Warning"}
    // Hyper-V channels added by installer when "--enable-hyperv" (§11.2)
    // iSM/iDRAC channel added when present (auto-detect)
  ],
  "logging": {
    "level": "Information",
    "dir": "C:\\ProgramData\\hyveman-agent\\logs",
    "rolling": "10MBx5"
  }
}
```
- **Hot-reload:** optional file watcher applies only the **safe subset**
  (levels, include/exclude IDs, intervals) without restarts; structural
  changes (URL, token, channels, caps) require service restart (a reload that
  can't be applied safely logs a warning and skips).
- **Pushed config** (DESIGN §4.1, §12) is Phase 2; the protocol version field
  is reserved now.

---

## 11. Packaging & installation

### 11.1 single-file exe
`dotnet publish -r win-x64 -c Release -p:PublishSingleFile=true
-p:SelfContained=true -p:IncludeNativeLibrariesForSelfExtract=true`
- **No `PublishTrimmed`** (default) — WMI reflection + wevtapi PInvoke are
  trim-hostile; the size win isn't worth the JIT-attribute rabbit hole.
- Output: one `hyveman-agent.exe` (~70 MB self-contained, acceptable for an
  internal tool).

### 11.2 install.ps1 (one-liner, idempotent)
```powershell
# downloaded/invoked per host; bootstrap params: backend URL + one-time reg token
./install.ps1 -BackendUrl https://hyveman.example.lan:8443 `
              -InstallToken reg_... `        # exchanged for an ingest token on first contact
              [-DataDir C:\ProgramData\hyveman-agent] `   # default; spool/state/logs derive from it
              [-EnableHyperV]
```
Steps (idempotent — re-run is safe):
1. Create dirs `ProgramData\hyveman-agent\{spool,state,logs}` (ACL to `SYSTEM`
   + Administrators; deny Users).
2. Copy `hyveman-agent.exe` to `C:\Program Files\hyveman-agent\`.
3. Write `agent.json` (bootstrap config with URL + reg token, default channel
   set; Hyper-V channels included only with `-EnableHyperV`).
4. Create the **Hyper-V operational channels** (`wevtutil sl /e:true` for the
   channels in App. B) **only when** `-EnableHyperV` is given. This is the
   *only* place channel/enabled state changes (the runtime invariant, §15.5).
   Each channel is probed with `wevtutil gl` first: channels the host doesn't
   have (e.g. `High-Availability-Admin` on non-clustered hosts) are omitted
   from `agent.json` and skipped with a warning — a missing channel never
   aborts the install (PS 5.1 note: native stderr must not become a
   terminating error under `$ErrorActionPreference = "Stop"`, so wevtutil
   calls run with EAP dropped and exit-code checks).
5. Service account: **`LocalSystem`** (default for now, DESIGN §13 #17). A
   dedicated least-privilege account (member of *Event Log Readers*) is a
   later option the installer will expose via a `-Account` flag; not used
   now to avoid per-host account provisioning on the small fleet.
6. Create/update the service via `New-Service` (create) or
   `Win32_Service.Change` (update) with `binPath= "<exe> --service"` — NOT
   `sc.exe`, whose native command line mangles embedded quotes under
   PowerShell 5.1 (`binPath= C:\Program ...`, exit 1639 on Server builds).
   Then `sc.exe` for `start= delayed-auto`, recovery settings from §4.3,
   description.
7. **Preflight** (§11.3); if it fails, roll back and abort (don't leave a
   half-installed service).
8. Start the service.
9. On first run, the agent exchanges the install token for a long-lived
   **ingest-scope token** via `POST /register` (DESIGN §13 #12); writes the
   ingest token into `agent.json` (ACL'd), discards the install token.

### 11.3 installer preflight (fail closed)
- OS ≥ Server 2019 (or Windows 10 1809+).
- "Audit Logon Events" policy enabled (else curated Security events are
  empty — warning, not abort; document the `auditpol` line).
- Network reachability to backend URL (TCP connect) + cert validation.
- Disk free ≥ `min_free_bytes` on the spool volume.
- `wevtapi.dll` + `mmi` (CIM) present (they always are, but fail loud if not).

### 11.4 uninstall.ps1
- Stop + `sc delete`. Remove `--EnableHyperV`-enabled channels **only if the
  agent enabled them** (leave them if something else uses them); delete
  `ProgramData` + `Program Files` dirs (optional `-KeepData` to retain
  spool/state for forensics).

---

## 12. Logging & observability (local)

- Structured logging via `ILogger` (Serilog rolling file, size-capped at
  `10MBx5`), **not** to the Windows Application Event Log by default (avoid
  recursive collection — the agent collecting its own log spam). A single
  `EventLog` source `HyvemanAgent` is registered **only** for a handful of
  lifecycle/critical messages (start/stop/preflight-fail/recovery-cap-hit),
  so operators see them in `System`/`Application` without the agent ingesting a
  chatty loop.
- **Self-collect its own EventLog source** is therefore a single channel the
  agent *does* ingest (so lifecycle messages reach the backend), guarded by a
  small `include_ids` allowlist to avoid recursion.

---

## 13. Security

- **TLS everywhere.** `ca_path` pins a private CA for lab networks; default
  validates against the system store. `validate_cert=false` is lab-only and
  logged loudly.
- **Token handling:** ingest-scope bearer token in `agent.json`, ACL'd to
  `SYSTEM` + Administrators only (NTFS ACL, **not** ciphertext — the agent
  must present it raw). Install one-time token is exchanged for the ingest
  token on first contact and discarded. Tokens never logged.
- **Service account: `LocalSystem`** (default, decided DESIGN §13 #17) — no
  new local accounts to provision on the small fleet. `LocalSystem` already
  has Security-log read and WMI access. A dedicated least-privilege account
  (member of *Event Log Readers*) is a documented **later option** the
  installer keeps open; not used now. Spool/state/config/logs ACL'd to
  `SYSTEM` + Administrators.
- **No channel modification at runtime** (invariant, §15.5) — also a security
  property: the agent can't reconfigure what it sees after install.
- **No secrets other than the token** leave the host. iDRAC creds live
  backend-side (DESIGN §7); agents never see them.
- **No inbound port.** Strictly client. Firewall posture unchanged (DESIGN §12 #1).

---

## 14. State & files layout

```
C:\ProgramData\hyveman-agent\
  agent.json              config (ACL: SYSTEM + Administrators)
  spool\                  durable log batches (cap: §4.1)
     1723040531123-0007a.json   ...
  state\
     <channel>.bookmark   per-channel bookmark (atomic; §6.6)
     <channel>.epoc        per-channel reset epoch (§6.7)
     quarantine\           non-retryable batches (§6.5)
     pid                   for double-instance detection (file lock)
  logs\                   agent's own rolling logs (§12)
```
- **Atomic writes everywhere:** bookmarks, epoch, spool batch files — write
  `.tmp`, flush, rename. Crash mid-write leaves a `.tmp` (cleaned on startup,
  never a corrupt final file).
- **Double-instance guard:** `state\pid` held with an exclusive `FileStream`
  lock; second instance of the service exits immediately (prevents two agents
  fighting one host's bookmarks/spool if mis-installed).
- **Single data dir rule** (DESIGN §9): everything under
  `C:\ProgramData\hyveman-agent\`. No registry-dependent runtime state.

---

## 15. Error handling & degraded modes

| Situation | Behavior | `degraded` flag |
|---|---|---|
| Backend unreachable | Spool batches; sender sleeps on backoff (CPU ~0); spool honors caps; oldest dropped when full | `spool_full` when cap hit |
| Backend slow/hanging | `send_timeout_ms` fires; bounded concurrency; no thread/handle leak (each `HttpResponseMessage` disposed) | — |
| Event firehose | In-mem channel fills → drop oldest event + `events_dropped++`; EvtSubscribe callback never blocks | `overrun` |
| Spool volume nearly full | Writes rejected at `min_free_bytes` floor; drop oldest; never the straw that fills the disk | `spool_full` |
| WMI provider hangs | `query_timeout_s` per query; prior facts re-sent `stale=true`; no thread accumulation | `wmi_degraded` |
| Bookmark invalid (log cleared/wrapped) | Resubscribe from "now"; bump epoch (§6.7); emit channel_reset synthetic event | `channel_reset` |
| Non-retryable 4xx | Quarantine batch; log loudly; surface in heartbeat | `quarantined` |
| Credential-class 4xx (401/403/404/410) | Keep spool file, retry slowly (5 min), log loudly, surface for re-registration | `auth_rejected` |
| Memory cap hit | **OS kills the agent** (Job Object). SCM restarts; 3rd crash → SCM stops → "agent silent" backend alert | — (agent dead) |
| Crash-on-start loop | SCM recovery cap (§4.3): stops retrying → service stopped → "agent silent" | — (agent dead) |
| Config invalid | `ValidateOnStart` fails → service fails to start with clear event-log entry | — |

Every cell above has a corresponding fault-injection test in §19.

---

## 16. Concurrency & backpressure model

- **Producers:** N ChannelSubscriber callbacks (thread-pool threads) → one
  bounded `Channel<LogEvent>` (single, not per-channel — order within a
  channel is preserved by reading from a single channel in FIFO; see note).
- **Single consumer:** BatchBuilder drains the channel → SpoolWriter (single
  writer, serializes disk writes simply). LogSender reads spool files
  concurrently up to `send_concurrency`.
- **Channel full → DropOldest** (not block). This is deliberate: the safe
  failure under flood is *drop with a count*, not *stall the EventLog service*.
- **Within-channel order** is preserved end-to-end (enqueue order in the
  in-mem channel → batch position → spool file → sender drains oldest file
  first). Cross-channel order is not guaranteed (backend indexes by time).

---

## 17. Startup & shutdown sequencing

**Startup**
1. Acquire `state\pid` file lock (double-instance guard); exit if held.
2. Place process in Job Object (memory + CPU caps) — *before* allocating large
   buffers, so the cap is real from the start.
3. Load + validate `AgentOptions` (`ValidateOnStart`); fail closed on invalid.
4. Initialize spooler; create dirs; clean `.tmp` leftovers from prior crash.
5. Per channel: read bookmark → `EvtCreateBookmark` → `EvtSubscribe`. On
   invalid-bookmark, run §6.7 reset path. Report startup event.
6. Start WmiFactCollector, HeartbeatTimer, senders.
7. Report "agent started" lifecycle event.

**Shutdown** (SCM `stop`)
1. Stop accepting on the in-mem channel; cancel all subscriptions
   (`EvtClose`).
2. Drain in-flight events to a final batch → spool flush.
3. Advance bookmarks for all flushed batches; persist.
4. LogSender: stop taking new files, finish current uploads (bounded wait, e.g.
   `shutdown_grace_s=10`), then exit. Unsent spool files stay on disk (sent
   on next start).
5. Update SCM checkpoint (`dwCheckPoint`/`dwWaitHint`) so Windows doesn't
   force-kill mid-flush.

Startup/shutdown have unit + integration tests (§19).

---

## 18. Build & project layout

```
src/Hyveman.Agent/
    Hyveman.Agent.csproj            net10.0; Worker template; PackageRef:
       Microsoft.Extensions.Hosting.WindowsServices
       Microsoft.Management.Infrastructure (CimSession)
       Serilog.Extensions.Hosting + rolling
    Program.cs                      generic host + Windows-service integration + Job Object init
    Options/
       AgentOptions.cs + validators
    Wevtapi/                         PInvoke layer (EvtSubscribe/Render/FormatMessage/Bookmark)
    Pipeline/
       ChannelSubscriber.cs
       BookmarkManager.cs
       BatchBuilder.cs   SpoolWriter.cs   LogSender.cs
       RuntimeMonitor.cs
       SpoolCaps.cs                  the two-cap check (§4.1) — unit tested
    Wmi/
       WmiFactCollector.cs
       HyperVQueries.cs             WQL table (ties to Hyper-V WMI ref sub-doc)
    Telemetry/
       HeartbeatTimer.cs   FactEnvelope.cs   TelemetrySender.cs
    Net/
       BackendClient.cs              HttpClient w/ retry/policy (Polly)
       EnvelopeBuilder.cs           §9 mapping
    Lifecycle/
       JobObjectHost.cs             PInvoke kernel32 job object
tests/Hyveman.Agent.Tests/          unit + property tests (§19.A/B)
tests/Hyveman.Agent.FaultHarness/   fault-injection harness (§19.B) — not shipped
```
- **Polly** for retry/backoff/jitter with bounded concurrency — standard,
  well-tested, beats a hand-rolled loop.
- **`HttpClient`** from `IHttpClientFactory` with named clients; lifetime
  managed by the host (no socket exhaustion).

---

## 19. Test strategy

"Never affects the host" **cannot be proven by testing alone** (§3). Tests
*verify* the containment mechanisms hold. Five layers, plus canary.

### 19.A Unit / property tests (pure logic)
- `SpoolCaps`: cap enforcement satisfies the property "free bytes never below
  min_free_bytes after any accepted write; total never above max_bytes"
  (property-based, random batch sizes).
- Bookmark round-trip: render → write → read → `EvtCreateBookmark` equality.
- Channel-reset / epoch: RecordID regression ⇒ epoch bump ⇒ record_id schema
  ⇒ dedup-distinct from pre-reset (string-equality test against the contract).
- Envelope builder: Windows event → envelope field mapping (App. A) for
  representative events (6008, 4624 LT2, 4624 LT3-dropped, 4625, 4740).
- Config validation: every invalid field rejected with a clear message.
- Handle-leak property over `ChannelSubscriber` across many subscribe/unsub
  cycles (RuntimeMonitor counters must net to zero).

### 19.B Fault-injection host tests (isolated throwaway VM, target OS build)
Each test is an automated scenario in `tests/Hyveman.Agent.FaultHarness`:
1. **Backend unreachable 4 h** → spool saturates (drops oldest, count grows,
   no dupes on reconnect), CPU ~0, no retry storm.
2. **Slow/hanging backend** (accepts TCP, never responds) → bounded
   concurrency, `send_timeout` fires, no thread/handle leak over the run
   (RuntimeMonitor net-neutral).
3. **Event firehose** ~50k ev/s via a test provider → in-mem channel drops
   oldest; `events_dropped` grows; **EventLog service latency measured
   unaffected** (EventLog's own reads still fast).
4. **WMI hang** (provider sleeps 5 min; simulated) → each query times out in
   `query_timeout_s`; no WMI thread accumulation; **Hyper-V's own WMI queries
   measured unaffected** (the citizenship proof for H2).
5. **Disk pressure** — spool volume to 99 % full externally → agent stops
   writing, flips `spool_full`, frees space by dropping oldest, **never** the
   process that tips the disk over.
6. **`taskkill /F` mid-spool-write / mid-bookmark-flush** → restart resumes
   from bookmark; no dupes (backend dedup confirms); no corrupt final files
   (`.tmp` cleaned).
7. **Channel clear** (`wevtutil cl System`) → §6.7 path: resubscribe from now,
   epoch bumps, new events sent under `e1:` record_ids, backend stores both
   pre- and post-clear events.
8. **Memory cap** — inject a synthetic allocator past
   `process_memory_bytes` → process killed by the Job Object; SCM restarts;
   after 3rd kill SCM stops → "agent silent" alert raised by backend.

### 19.C Soak test (non-optional for "never")
Run scenario 1 (backend down) + nominal event flow unattended for **3–7 days**.
Graph: working set, GC heap, handle count, thread count, spool bytes, dropped
count, CPU. Any **monotonic upward trend is a bug** regardless of whether short
tests passed — slow leaks are what moment-testing never catches.

### 19.D Adversarial input
- Huge `EvtFormatMessage` strings (≥1 MiB) → `raw` truncation to
  `max_raw_bytes` with marker; `message` still complete; batch still sent.
- Weird provider names / no provider / null `Channel` (synthetic).
- bookmarks: file missing, truncated, from a wrapped channel → §6.7 path; no
  flood, alert raised.
- 4xx from backend (synthesize `400`) → quarantine, no infinite retry loop;
  `5xx` → retries per backoff; both CPU-bounded by the job cap.

### 19.E Invariants asserted continuously in tests
- Never opens a listening port (`netstat` snapshot diff before/after).
- Never writes outside `C:\ProgramData\hyveman-agent\` + the event-log
  registration for the single lifecycle source (filesystem audit / ETW
  filesystem trace).
- Never modifies channel `Enabled` state at runtime (compare
  `wevtutil el /e:true` listing before/after service start).
- Never writes to the registry at runtime (registry audit diff).

### 19.F Canary (real hosts)
All of the above is lab. First **real** deployment is one non-critical host
for N days, watching resource graphs + alerts + the "agent silent" heartbeat
alert, before fleet rollout.

---

## 20. Open questions (to resolve before/while building)

1. **record_id epoch scheme (§6.7)** — **decided (DESIGN §13 #15):** the
   epoch-prefixed `record_id` is the contract. No backend schema change; the
   agent constructs `record_id` per §6.7.
2. **Endpoint shape** — **decided (DESIGN §13 #16):** two endpoints,
   `/ingest/logs` (idempotent) and `/ingest/telemetry` (latest-wins), as
   specified in §9.
3. **Registration flow** — **resolved** in `docs/PROTOCOL.md` §5 (and decision
   DESIGN §13 #18): `POST /register` with a single-use `reg_` token returns a
   long-lived `agt_` ingest-scope token + `source_id`; reinstall reuses the
   `source` by `(kind, hostname)` and gets a fresh token. Agent discards the
   `reg_` token after.
4. **Hyper-V exact WQL set** — produce the Hyper-V WMI reference sub-doc
   (companion to DESIGN §14 #3) with sample outputs from a Server 2019 host,
   pinning the `Msvm_*` classes/properties for Server 2019.
5. **Trimming** — **decided: stays off.** `PublishTrimmed` is not used; WMI
   reflection + wevtapi PInvoke are trim-hostile and the ~70 MB
   self-contained exe is acceptable for an internal tool. Not revisited.
6. **Hot-reload safe subset** — enumerate exactly which fields are hot
   vs cold (likely hot: levels, include/exclude IDs, intervals, heartbeat
   interval; cold: URL, token, channels, all caps).

---

## Appendix A — Windows event → envelope field mapping

| Envelope field | Windows source | Notes |
|---|---|---|
| `record_id` | `EventRecordID` | bare, or `e<epoch>:<id>` after reset (§6.7) |
| `dedup_scope` | channel name | DESIGN §13 #11; config entry name for synthetic/self-collect entries that map onto a shared channel via a provider filter (PROTOCOL §11.1) |
| `time` | `TimeCreated` (system event time) | converted to UTC ISO-8601 |
| `severity` | `Level` (1=Critical…5=Verbose) | integer; omitted when the event's Level is unspecified (0) |
| `facility` | provider name (`System.Provider.Name`) | DESIGN §5.1 (= provider, NOT channel); null when the provider name is absent (never a literal `"unknown"`) |
| `message` | `EvtFormatMessage(EVT_FORMAT_MESSAGE_MESSAGE)` | rendered user-facing text |
| `fields.channel` | channel name | promoted to indexed column |
| `fields.event_id` | `EventID` | promoted |
| `fields.task` | `Task` | promoted |
| `fields.opcode` | `Opcode` | promoted |
| `fields.keywords` | `Keywords` (hex string) | promoted |
| `fields.provider_guid` | `Provider.Guid` | correlation |
| `fields.computer` | `Computer` | = host name |
| `fields.activity_id` | `ActivityId` | correlation |
| `fields.process_id`/`thread_id` | `Execution.ProcessID/ThreadID` | when present |
| `fields.event_data` | parsed `EventData` `Data{Name=...}` kv | used by 4624 LT filter (§6.4) |
| `raw` | `EvtFormatMessage(EVT_FORMAT_MESSAGE_XML)` | capped (§9.3) |

## Appendix B — default channels (written by `install.ps1`)

| Channel | Level | Notes |
|---|---|---|
| System | Warning | always |
| Application | Warning | always |
| Security | (curated IDs only) | §6.4; requires Audit Logon Events |
| Microsoft-Windows-Hyper-V-VMMS-Admin | Warning | `--EnableHyperV` |
| Microsoft-Windows-Hyper-V-Worker-Admin | Warning | `--EnableHyperV` |
| Microsoft-Windows-Hyper-V-Compute-Operational | Warning | `--EnableHyperV` |
| Microsoft-Windows-Hyper-V-Config-Operational | Information | `--EnableHyperV` (operational) |
| Microsoft-Windows-Hyper-V-StorageVSP-Admin | Warning | `--EnableHyperV` |
| Microsoft-Windows-Hyper-V-High-Availability-Admin | Warning | `--EnableHyperV` (clustered; skipped with a warning if absent — install never aborts) |
| Microsoft-Windows-Hyper-V-Image-Management-Operational | Information | `--EnableHyperV` |
| Microsoft-Windows-FailoverCluster*/… | Warning | if clustered (auto-detected) |
| storage driver channels (storahci/percsas/…) | Warning | auto-detected by provider presence |
| iDRAC/iSM channel | Warning | auto-detected; skipped if absent (non-Dell) |
| `HyvemanAgent` (own lifecycle source) | Information | single lifecycle source; `include_ids` allowlist to prevent recursion (§12) |

---

*Companion artifacts (out of scope here): wire-protocol spec (DESIGN §14 #1),
agent-config spec (DESIGN §14 #2), Hyper-V WMI query reference (DESIGN §14 #3).*