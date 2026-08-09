# Hyveman — Known Defects & Contract Divergences

**Status:** open findings from a full code-vs-docs conformance audit (2026-08-09)
**Scope:** `hyveman-api`, `hyveman-agent`, `hyveman-web` checked against
`DESIGN.md`, `PROTOCOL.md`, `API.md`, `AGENT.md`, `FRONTEND.md`.

This document tracks *deviations from the design contracts* — code that does not
yet do what the contracts already say. Contract changes belong in the contract
docs, not here.

All 125 API tests passed at the time of the audit; **none** of the defects below
are covered by an existing test. Three findings (D1a, D6, D2) were verified by a
throwaway xUnit harness run against a real SQLite database; those are marked
**verified**. The rest are established by reading the code against the spec and
are marked **by inspection** — they should be reproduced before a fix is
accepted.

Severity: **P1** = data loss, silent wrong data, or a broken core feature;
**P2** = feature non-functional, wrong data displayed, or a security weakening;
**P3** = cosmetic, doc drift, or robustness with no current impact.

| ID | Sev | Component | Summary | Evidence |
|---|---|---|---|---|
| [D1](#d1) | P1 | api | Derived data attributed to wrong events in mixed dedup/new batches | ~~verified~~ **fixed** |
| [D2](#d2) | P1 | api | Second resolve of an alert key throws; cascades to telemetry 500s | ~~verified~~ **fixed** |
| [D3](#d3) | P1 | api | Alert evaluator state is per-request: cooldown dead, alerts never auto-resolve | ~~inspection~~ **fixed** |
| [D4](#d4) | P1 | api | Redfish collections never expanded — no CPU/DIMM/disk/controller health | ~~inspection~~ **fixed** |
| [D5](#d5) | P1 | api | Event search skips a row at every page boundary | ~~verified~~ **fixed** |
| [D6](#d6) | P1 | api | Web session lifetime compounds without bound | inspection |
| [D7](#d7) | P2 | api | `heartbeat_ok: false` coerced to `null` | verified |
| [D8](#d8) | P2 | api | Duplicate, never-resolving agent-silent alerts from the reconcile path | inspection |
| [D9](#d9) | P2 | api | Reconcile pass ignores maintenance windows | inspection |
| [D10](#d10) | P2 | api | Outbox rows stuck in `sending` are never recovered | inspection |
| [D11](#d11) | P2 | agent | VM CPU percentage divided by 100 — always renders 0% | inspection |
| [D12](#d12) | P2 | api | `Status.HealthRollup` ignored by the health mapper | inspection |
| [D13](#d13) | P2 | api | Overview reports iDRAC poll state from stale components, not `poll_status` | inspection |
| [D14](#d14) | P2 | api | Overview Hyper-V state hard-coded to `unknown` | inspection |
| [D15](#d15) | P2 | api | Audit-log pagination never returns a cursor | inspection |
| [D16](#d16) | P2 | api | Vault key silently regenerated when missing | inspection |
| [D17](#d17) | P2 | api | Vault key file gets no ACL on Windows | inspection |
| [D18](#d18) | P2 | api | Loopback is always trusted for first-run setup | inspection |
| [D19](#d19) | P2 | agent | Pinned-CA mode skips hostname validation | inspection |
| [D20](#d20) | P2 | api | Login ceremony sets user verification to `Discouraged` | inspection |
| [D21](#d21) | P2 | api | Health history is never downsampled | inspection |
| [D22](#d22) | P2 | agent | First post-clear event is dropped | inspection |
| [D23](#d23) | P2 | agent | Channel reset detection dies after a retried subscribe | inspection |
| [D24](#d24) | P3 | — | Ten smaller items | inspection |

---

<a id="d1"></a>
## D1 — Derived data attributed to the wrong events in mixed batches (P1, verified → fixed)

**Location:** `hyveman-api/src/Hyveman.Application/LogIngestService.cs:58`
**Contract:** PROTOCOL §6.6, API.md §6.3.

```csharp
var acceptedItems = valid.Take(result.Accepted).ToList();
```

`IEventStore.InsertBatchAsync` returns only counts, so `Take(accepted)` assumes
the accepted items are the first N of the batch — true only when a batch is
entirely new or entirely duplicate. `acceptedItems` feeds the alert evaluator
and the `logon_stats` aggregate.

**Failure scenario.** A batch is committed, the response is lost, and the agent
retries the spool file with one new item appended. Item 1 dedupes, item 2 is
accepted, `result.Accepted == 1`, and `Take(1)` yields **item 1** — the
duplicate. The replayed logon is counted a second time and the new one is never
counted, which is exactly what §6.6 forbids. The evaluator likewise re-fires on
the replay and misses the new event. Retry-after-partial-commit is routine, not
exotic.

**Fix.** Return the accepted subset from the store instead of reconstructing it
from a count; `EventStore.cs:46` already knows per row (`affected == 1`). The
wire response shape is unaffected.

**Test to add.** Ingest `[A]`, then `[A, B]`; assert `accepted==1 && deduped==1`,
that `logon_stats` counts one success total, and that the evaluator saw `B`.

**Status: FIXED (2026-08-09).** `IngestResult` now carries `AcceptedItems` — the
exact accepted subset in batch order — collected in `EventStore.InsertBatchAsync`
where each row's `affected == 1` is already known, and `LogIngestService`
consumes `result.AcceptedItems` instead of `valid.Take(result.Accepted)`. The
wire response shape is unchanged. Regression coverage per the test above:
`WebApi_MixedBatch_DerivedData_AttributedToAcceptedItemsOnly` (API-level: ingests
`[A]` then `[A, B]`, asserts `accepted==1 && deduped==1`, one `logon_stats`
success, and that a 4740-only rule fired on `B` — i.e. the evaluator saw the
accepted item, not the replay), plus store-level `AcceptedItems` assertions in
`InfrastructureTests` (`Events_IdempotentInsert_DedupesAndSearches`). Both were
verified to fail against the old `Take(count)` code.

---

<a id="d2"></a>
## D2 — Second resolve of an alert key throws, cascading to telemetry 500s (P1, verified)

**Location:** `hyveman-api/src/Hyveman.Infrastructure.Sqlite/Migrations.cs:194`
(`UNIQUE(key, status)`) with `AlertStores.cs:40` and
`AlertEvaluatorService.cs:301`.
**Contract:** API.md §9.3 — "a resolved occurrence to be followed by a new
occurrence without losing history".

The `alerts` table carries `UNIQUE(key, status)`. The lifecycle is:

| cycle | row | status |
|---|---|---|
| 1 fire | `al_1` | `active` |
| 1 resolve | `al_1` | `resolved` |
| 2 fire | `al_2` | `active` (no conflict — status differs) |
| 2 resolve | `al_2` | → `resolved` — **collides with `al_1`** |

`ResolveAsync` issues a bare `UPDATE alerts SET status='resolved'`, which
violates the constraint on the second cycle. Verified: the update throws
`SQLite Error 19: UNIQUE constraint failed`.

**Why this is P1, not a nuisance.** The heartbeat path reaches it on ordinary
operation. `TelemetryService.ProcessAsync:68` calls
`OnHeartbeatSilenceChangedAsync(silent:false)` on **every** heartbeat, and that
calls `ResolveAsync`. So the second time an agent goes silent and comes back,
the resolve throws, the exception is unwrapped by
`TelemetryService.ProcessAsync` (no try/catch there), and
`AgentProtocolMiddleware` converts it to `500 internal`. The agent then treats
telemetry as retryable and never succeeds again: heartbeats stop being recorded,
the source is marked silent, and the silence alert can never clear. A recoverable
blip becomes a permanent false "agent silent" plus a hot retry loop.

**Fix.** Decide what uniqueness is actually wanted. `UNIQUE(key, status)` cannot
express "at most one *live* alert per key" because it also constrains the
resolved history. Options: a partial unique index over live statuses only
(`CREATE UNIQUE INDEX ... ON alerts(key) WHERE status IN ('active','acknowledged','silenced')`),
or drop the constraint and rely on `FindLiveAsync`. Either way, wrap the
evaluator call in `TelemetryService` so a derived-alerting failure can never
fail an accepted telemetry request (the same guard `LogIngestService:63` already
applies to log ingest).

**Test to add.** Two full fire→resolve cycles on one key; assert both resolves
succeed and history retains two rows. Plus: a telemetry POST must return 200
even when the evaluator throws.

**Status: FIXED (2026-08-09).** Migration V5 rebuilds `alerts` without the
`UNIQUE(key, status)` constraint and installs a partial unique index over live
statuses only (`ux_alerts_live_key ... WHERE status IN ('active','acknowledged','silenced')`),
plus a plain `idx_alerts_key` for the cooldown lookup. Resolved history is now
unconstrained while "at most one live alert per key" is still enforced at the
schema level — now across active/acknowledged/silenced, which the old
constraint could not express. `TelemetryService.ProcessAsync` now guards the
`OnHeartbeatSilenceChangedAsync` call exactly like `LogIngestService` guards
`OnEventsAcceptedAsync`, so a derived-alerting failure can never fail an
accepted telemetry request. Regression coverage: `Alerts_TwoFireResolveCycles_SameKey_KeepHistory`
and `Alerts_PartialIndex_EnforcesOneLivePerKey` (store-level, real SQLite) and
`Telemetry_Returns200_WhenAlertEvaluatorThrows` (API-level, DI override), plus
`HealthAlert_TwoWarningToOkCycles_ResolveAndKeepHistory` which drives the
second resolve of a key through the real evaluator and DI container. All four
were verified to fail against the pre-fix code — the first reproduced the
exact `SQLite Error 19: UNIQUE constraint failed` from the field report.

---

<a id="d3"></a>
## D3 — Alert evaluator state is per-request: cooldown dead, alerts never auto-resolve (P1, by inspection)

**Location:** `hyveman-api/src/Hyveman.Api/Program.cs:118`
(`AddScoped<IAlertEvaluator, AlertEvaluatorService>()`) vs
`AlertEvaluatorService.cs:29-32`.
**Contract:** DESIGN §4.4 (per-rule cooldown, dedup), API.md §9.3.

The evaluator holds four `ConcurrentDictionary` fields — `_lastRollup`,
`_lastComponentState`, `_lastThreshold`, `_lastFired` — whose names and type
imply process-wide state. But the service is registered **scoped**, and every
caller creates a fresh scope: `HardwarePollingService.TickAsync:78` news up a
scope per poll tick, ingest resolves per request, reconciliation per pass. Each
one therefore gets an evaluator whose dictionaries are **empty**.

Consequences, all of which follow directly from the code paths:

1. **Cooldown never applies.** `_lastFired` is empty at every entry, so the
   `rule.CooldownS` check at `AlertEvaluatorService.cs:268` is unreachable.
2. **Component alerts never auto-resolve.** `prev` defaults to `"unknown"`
   (line 81), so the resolve branch `else if (prev is "warning" or "critical")`
   (line 97) can never be taken. A DIMM that recovers keeps its alert Active
   forever.
3. **Rollup alerts never auto-resolve.** Same shape at lines 71-73 and 117.
4. **Threshold alerts never auto-resolve.** `was` is always `false`
   (line 171), so `!crossing && was` at line 177 is unreachable; and every poll
   while over-threshold re-enters `FireAsync` (suppressed to a count bump by
   `FindLiveAsync`, so the symptom is a silently inflated `count`).

Net effect: hardware alerts are one-way. After a transient warning the console
stays red until someone acknowledges by hand, which trains operators to ignore
it.

**Fix.** Either register the evaluator as a singleton that opens scopes for its
stores, or — better for a process that can restart — stop keeping transition
state in memory at all. The previous state is already durable: `components`
holds the last state per component (read it before `ReplaceComponentsAsync`),
and cooldown can key off the resolved alert's `last_seen`. Note D2 must be
fixed first, or making resolution reachable will start throwing.

**Test to add.** Poll a host warning→ok across two ticks through the real DI
container and assert the alert reaches `resolved`; fire twice inside a cooldown
window and assert one alert.

**Status: FIXED (2026-08-09).** The evaluator is now stateless: the four
`ConcurrentDictionary` fields are gone, and every transition input is read from
durable stores. The previous component/rollup state comes from the `components`
table — `HardwarePollingService` now calls the evaluator *before*
`ReplaceComponentsAsync` (contained in its own try/catch, so a derived-alerting
failure can no longer lose the accepted poll result or mark it a poll failure);
the previous threshold crossing is the live alert itself (`FindLiveAsync`); and
cooldown keys off the most recent occurrence's `last_seen` via the new
`IAlertStore.GetLatestAsync`. Key construction is unified behind one
`AlertKey` helper so the paths cannot drift again. The scoped registration is
now correct as-is — no singleton needed. Regression coverage:
`HealthAlert_TwoWarningToOkCycles_ResolveAndKeepHistory` (two full warning→ok
cycles across fresh DI scopes; both resolve and history retains two rows — the
second cycle also proves D2 through the real container) and
`EventRule_RefireInsideCooldown_Suppressed` (refire inside the window from a
fresh scope produces no new alert), both API-level; the store-level cooldown
lookup is covered by `Alerts_TwoFireResolveCycles_SameKey_KeepHistory`. All
were verified to fail against the pre-fix code (no auto-resolve and no cooldown
across fresh evaluator instances).

---

<a id="d4"></a>
## D4 — Redfish collections are never expanded: no CPU, DIMM, disk or controller health (P1, by inspection)

**Location:** `hyveman-api/src/Hyveman.Infrastructure.Redfish/DellRedfishProvider.cs:41-60,119-127`
**Contract:** DESIGN §1 (primary goal: at-a-glance hardware health), §2.1, §4.2;
API.md §9.1.

`GET /redfish/v1/Systems/System.Embedded.1/Processors` returns a Redfish
*collection*: its `Members` array contains link objects
(`{"@odata.id": "/redfish/v1/.../Processors/CPU.Socket.1"}`), not the resources
themselves. Member resources are only inlined when the request asks for it
(`?$expand=*($levels=1)`), which this code does not.

The `members(...)` lambda skips any member without a `Name` property
(line 51-52), so **every** processor and memory member is skipped. The same
applies to the Dell OEM `DellPhysicalDisk` / `DellController` member arrays at
lines 123-126.

What actually reaches the component table: temperatures, fans and PSUs (the
`Thermal`/`Power` resources *do* embed full objects inline, so those paths are
correct), plus one synthetic `System.Embedded.1` row. **CPUs, DIMMs, physical
disks and storage controllers — the components DESIGN §4.4 names as the
motivating examples ("predictive disk failure, PSU lost, DIMM error") — are
never collected, and the health rollup silently omits them.** The dashboard can
show a green host with a failed disk.

The OEM path is separately suspect: Dell exposes physical disks under
`/redfish/v1/Systems/System.Embedded.1/Storage/<controller>/Drives/...` (or the
`/redfish/v1/Dell/...` OEM tree), not as an `Oem.Dell.DellPhysicalDisk` property
of the Chassis resource. I could not verify this against hardware.

**Context.** DESIGN §14 #3 lists the "Redfish mapping table — exact endpoints per
data point ... with sample payloads from one fleet iDRAC" as a **required
artifact that has not been produced**. This code was written without it, and it
shows. Produce that artifact against a real fleet iDRAC before fixing, otherwise
the fix is guesswork too.

**Fix.** Either request `$expand` on collections, or follow each member's
`@odata.id` (5 collections × N members per host per 60 s is fine at this fleet
size). Then re-derive the OEM paths from a captured payload.

**Test to add.** A recorded iDRAC payload fixture per resource, asserting the
normalized component list contains the expected CPU/DIMM/disk rows. Note the
existing `InfrastructureTests` Redfish coverage exercises the mapper on
already-inlined JSON, which is why this is invisible today.

**Status: FIXED (2026-08-09).** Verified against the live fleet iDRAC first
(HOST-A, 10.x.x.x, iDRAC9 / PowerEdge R7415): every collection
(`Processors`, `Memory`, `Storage`) returns bare link objects in `Members`,
and the Chassis `Oem.Dell` carries only `DellChassis` — no
`DellPhysicalDisk`/`DellController` — confirming the OEM path was dead code.
The captured payloads are recorded in the §14 #3 artifact,
`docs/REDFISH-MAPPING.md`. `DellRedfishProvider` now follows each member's
`@odata.id` for Processors and Memory, and walks the real storage tree
(`Systems/.../Storage/<controller>` → `Drives/<id>`) for controllers and
physical disks, replacing the chassis-OEM path; a drive with
`FailurePredicted: true` escalates to `warning` even when firmware reports
`Status.Health: OK`. Regression coverage, all fixture-based on the recorded
payloads: `Poll_RealFleetPayloads_NormalizesAllComponentTypes` (asserts CPU /
DIMM / controller / disk rows plus the member-fetch request set and the
absence of the chassis fetch), `Poll_PredictiveFailure_EscalatesDiskToWarning`,
`Poll_CriticalComponent_DrivesRollupToCritical`, and the retained failure
path. The two new tests were verified to fail against the pre-fix provider
(the inline-only parse skipped every CPU/DIMM/disk/controller member).

---

<a id="d5"></a>
## D5 — Event search skips a row at every page boundary (P1, verified)

**Location:** `hyveman-api/src/Hyveman.Application/EventsService.cs:26`
**Contract:** API.md §7.2, §5.2; FRONTEND §8.3.

The service uses the "+1 probe row" technique but then encodes `nextCursor`
from `page.Items[^1]` — the last element of the **untrimmed** result, i.e. the
probe row that was deliberately withheld from the client. Page 2 asks for rows
strictly *after* that cursor, so the probe row is delivered by neither page.

**Verified with 3 events at `limit=2`:** page 1 returned `[msg3, msg2]` with
`hasMore=true`; page 2 returned **nothing**. `msg1` is unreachable through the
API. With larger result sets the loss is one row per boundary (rows 51, 102,
153, … at the default page size), and whenever the remainder is exactly one
page-size the final page is empty while `hasMore` was true.

This is silent — no error, no gap indicator — and it quietly negates the
no-gap guarantee the whole ingest pipeline exists to provide (AGENT §1.1). The
events are stored correctly; only the operator's view of them drops rows.

**Fix.** Encode the cursor from the last **returned** row
(`page.Items[limit - 1]`, or `items[^1]` after mapping).

**Test to add.** 3 events, `limit=2`, assert the union of both pages is all 3
with no duplicates. API.md §6.8/§14 has no pagination fixture at all today.

**Status: FIXED (2026-08-09).** `EventsService.SearchAsync` now encodes
`NextCursor` from `items[^1]` — the last row actually returned to the client —
instead of `page.Items[^1]`, the +1 probe row deliberately withheld by the
`Take(limit)` trim. Regression coverage per the test above:
`WebApi_EventSearch_PagesCoverEveryRow_NoGapsNoDuplicates` (API-level: ingests
3 events, walks `limit=2` through both pages scoped to the agent's source,
asserts page 1 is `[pg-3, pg-2]` with `hasMore`, the cursor resolves to the
last *returned* row, page 2 delivers `pg-1` alone with `hasMore=false` and a
null cursor, and the union of both pages is all 3 rows with no duplicates).
The test was verified to fail against the pre-fix code — page 2 came back
empty, exactly the field report.

---

<a id="d6"></a>
## D6 — Web session lifetime compounds without bound (P1, by inspection)

**Location:** `hyveman-api/src/Hyveman.Infrastructure.Sqlite/ConfigStores.cs:128`
**Contract:** DESIGN §8 and API.md §8.2 — "14-day sliding expiry".

```csharp
var newExpiry = now.Add(session.ExpiresAt - session.CreatedAt);
```

The slide window is recomputed on every validation as `expires - created`, but
only `expires_at` is written back — `created_at` stays fixed. The window
therefore grows on every request:

| validated at | window used | new expiry |
|---|---|---|
| t=1d | 14d | 15d |
| t=2d | 15d | 17d |
| t=3d | 17d | 20d |

After a month of normal use a single request extends the session by months; the
server-side record becomes effectively immortal. A stolen session cookie stays
valid far beyond the 14 days the design promises, and `CleanupExpiredAsync`
never reaps it.

**Second, opposite defect in the same feature.** The browser cookie is issued
once at login with a fixed `MaxAge = 14 days` (`AuthController.cs:141`) and is
never re-issued on use, so the cookie is *not* sliding at all. The user is
forced to re-authenticate 14 days after login regardless of activity — the
opposite of the documented "effectively never re-login while in regular use".
So the server record slides too much and the cookie slides not at all.

**Fix.** Use a fixed configured lifetime for the slide
(`newExpiry = now + SessionLifetime`) and re-append the cookie on each
successful slide (or on a threshold, to avoid a `Set-Cookie` per request).

**Test to add.** Validate a session repeatedly across simulated days and assert
`expires_at - now` never exceeds 14 days.

---

<a id="d7"></a>
## D7 — `heartbeat_ok: false` is coerced to `null` (P2, verified)

**Location:** `hyveman-api/src/Hyveman.Protocol/ProtocolValidation.cs:293`
**Contract:** PROTOCOL §7.1 (`true|false|null`), DESIGN §2.3.

```csharp
if (vm.TryGetProperty("heartbeat_ok", out var hbProp) && hbProp.ValueKind == JsonValueKind.True)
```

`JsonValueKind.False` fails the guard, so an explicit `false` is stored as
`null`. The agent does send `false` for a running VM whose Integration Services
heartbeat has failed (`HyperVQueries.cs:76`), and the UI distinguishes the three
states (`HostDetailPage.tsx:124` → `'—' : 'OK' : 'Lost'`). A degraded VM is
therefore indistinguishable from one never reported on — the exact failure the
field exists to surface.

**Fix.** `hbProp.ValueKind is JsonValueKind.True or JsonValueKind.False`.
Everything downstream already handles all three states.

---

<a id="d8"></a>
## D8 — Duplicate, never-resolving agent-silent alerts (P2, by inspection)

**Location:** `AlertEvaluatorService.cs:231` vs `:136`
**Contract:** API.md §9.2/§9.3.

The alert key is `{rule}|{hostId ?? "-"}|{sourceId ?? "-"}|{fingerprint}`. The
two paths that fire agent-silent alerts disagree on `hostId`:

- `HeartbeatMonitor` → `OnHeartbeatSilenceChangedAsync` passes `host?.Id`
  (line 136) → key `rul_x|hst_1|src_1|heartbeat:silent`;
- `ReconcileAsync` passes `hostId: null` unconditionally (line 231) → key
  `rul_x|-|src_1|heartbeat:silent`.

So a silent agent with an associated host accumulates **two** live alerts once
the 6-hourly reconcile runs. Worse, the clear path
(`OnHeartbeatSilenceChangedAsync(silent:false)`, line 144) resolves using
`host?.Id` only — so the reconcile-created alert is **never resolved** and stays
Active forever.

**Fix.** Resolve the host in `ReconcileAsync` (it already has `hosts` injected)
and use the same key construction in both paths; better, extract one
`HeartbeatAlertKey(rule, sourceId)` helper so the two can't drift again.

---

<a id="d9"></a>
## D9 — Reconciliation ignores maintenance windows (P2, by inspection)

**Location:** `AlertEvaluatorService.cs:270` and `:231`
**Contract:** API.md §9.2 — the heartbeat monitor clears/creates silence alerts
"according to the configured rule **and maintenance windows**"; DESIGN §4.4.

`FireAsync` only consults maintenance windows when `hostId is not null`
(line 270). `ReconcileAsync` fires heartbeat alerts with `hostId: null`
(line 231), so the suppression check is skipped entirely on that path.

`HeartbeatMonitor.RunOnceAsync` does check windows correctly
(`MaintenanceAndMonitor.cs:100-102`), so the effect is: rebooting a host inside
a maintenance window is suppressed by the monitor, then re-raised by the next
reconcile pass — and per D8 that alert never resolves. Host reboots during
maintenance are the canonical reason maintenance windows exist.

**Fix.** Fold the window check into `FireAsync` for source-scoped alerts too, by
resolving the source's host before the check.

---

<a id="d10"></a>
## D10 — Outbox rows stuck in `sending` are never recovered (P2, by inspection)

**Location:** `hyveman-api/src/Hyveman.Infrastructure.Sqlite/AlertStores.cs:329-347`
**Contract:** API.md §9.4 — "The outbox is durable, retryable ... It prevents a
process crash between alert commit and Telegram/webhook delivery from losing the
notification."

`DequeueDueAsync` flips rows to `status='sending'` before handing them to the
dispatcher. Nothing ever moves them back. If the process stops between dequeue
and `MarkResultAsync` — a restart, a deploy, a crash — the row stays `sending`
forever: `DequeueDueAsync` only selects `pending`, and no reaper exists. The
outbox closes the loss window between alert commit and enqueue but opens a new
one between dequeue and result.

**Fix.** Reap on startup and in `MaintenanceJob.RunCleanupAsync`: reset
`sending` rows older than a few minutes back to `pending` (they are retryable by
construction; at-least-once delivery is the right trade for alerting).

---

<a id="d11"></a>
## D11 — VM CPU percentage divided by 100 (P2, by inspection)

**Location:** `hyveman-agent/src/Hyveman.Agent/Wmi/HyperVQueries.cs:83`
**Contract:** PROTOCOL §7.1 (`"cpu_pct": 12.3`).

```csharp
CpuPct = cpu is null ? null : Math.Round(cpu.Value / 100.0, 2),
```

`Msvm_SummaryInformation.ProcessorLoad` (request ID 101) is already a percentage
0–100 — as the constant's own comment says (`ProcessorLoad (uint16, %)`,
line 20). Dividing by 100 sends 0.45 for a VM at 45%.

The frontend then renders it with `formatPercent`, which rounds and appends `%`
(`format.ts:13`). Round-trip: **every VM under 150% load displays as `0%`**.

**Fix.** Drop the division. Add an envelope-mapping unit test with a known
`ProcessorLoad` value — AGENT §19.A calls for exactly this class of test.

---

<a id="d12"></a>
## D12 — `Status.HealthRollup` ignored by the health mapper (P2, by inspection)

**Location:** `DellRedfishProvider.cs:174-193`
**Contract:** DESIGN §2.1 names `Status.HealthRollup` as *the* primary hardware
health signal; §4.2 repeats it for the system resource.

`MapHealth` reads `Status.Health`, then falls back to `Status.State`. It never
reads `Status.HealthRollup`. `ReadDetail` puts `HealthRollup` into the human
detail string (line 200-201), so the value is fetched and then discarded for
state purposes.

On Dell systems `Status.Health` on the top-level System resource commonly
reflects only that resource, while `HealthRollup` aggregates subsystems — which
is why the design chose it. Combined with D4 (subsystem components missing
entirely), the host rollup can report OK while the machine has a failed
component.

**Fix.** Prefer `HealthRollup` over `Health` for resources that expose it,
at minimum for the System and Chassis resources.

---

<a id="d13"></a>
## D13 — Overview reports iDRAC poll state from stale components (P2, by inspection)

**Location:** `hyveman-api/src/Hyveman.Application/OverviewService.cs:72-77`
**Contract:** FRONTEND §8.1 ("last successful iDRAC poll age/failure state"),
API.md §9.1, FRONTEND §1 ("make stale data obvious").

```csharp
Idrac = host.IdracUrl is null ? null : new IdracStatusDto
{
    Configured = true,
    LastPoll = rollupAt,               // = max(component.last_seen)
    LastPollOk = components.Count > 0, // = "we have some components"
},
```

Migration V3 added a `poll_status` table (`last_poll`, `last_success`,
`last_error`, `failures`) precisely for this, `IPollStatusStore` is registered,
and `HardwarePollingService` writes it faithfully on both success and failure.
`OverviewService` never reads it.

So a host whose iDRAC has been unreachable for hours still reports
`LastPollOk = true` with a `LastPoll` timestamp frozen at the last *successful*
component write. The dashboard cannot distinguish "healthy and current" from
"we stopped being able to ask". That is the specific failure mode the stale-data
requirement targets.

**Fix.** Inject `IPollStatusStore` into `OverviewService` and populate
`LastPoll`/`LastPollOk`/failure count from it.

---

<a id="d14"></a>
## D14 — Overview Hyper-V state hard-coded to `unknown` (P2, by inspection)

**Location:** `OverviewService.cs:70` — `HyperVState = "unknown"`
**Contract:** DESIGN §4.5 and FRONTEND §8.1 — each tile shows health "split into
Hardware / OS / Hyper-V".

The VM facts needed to compute it are present (`vms` table, populated by
`TelemetryService`), but the tile's Hyper-V segment is a constant. One third of
the documented tile is permanently blank.

**Fix.** Derive from the host's VM rows: any VM with `heartbeat_ok == false`
→ warning/critical; `stale` facts → unknown/stale; no VMs → `ok` or `n/a`.
Depends on D7 being fixed first, or the `false` signal never arrives.

---

<a id="d15"></a>
## D15 — Audit-log pagination never returns a cursor (P2, by inspection)

**Location:** `hyveman-api/src/Hyveman.Application/AdminServices.cs:127-135`
**Contract:** API.md §5.2, §7.

`AuditService.ListAsync` sets `Items` and `HasMore` but never `NextCursor`,
which stays `null`. The plumbing exists on both sides: `AuditController` accepts
a `cursor` parameter, `IAuditStore.ListAsync` takes one, and `AuditStore`
implements it correctly (`id < @CId` with `ORDER BY id DESC`,
`ConfigStores.cs:31`). Only the response side is unwired.

The frontend consumes it: the "next page" button is enabled by `hasMore` but its
handler is a no-op because `nextCursor` is `undefined`
(`AuditPage.tsx:71,128`). The audit log is stuck on page 1 behind a control that
looks functional.

Secondary: `HasMore = page.Count >= limit` is computed without a `+1` probe, so
an exactly-full final page claims another page exists.

**Fix.** Mirror the corrected `EventsService` shape (see D5): fetch `limit + 1`,
trim, set `NextCursor` from the last returned row's `id`.

---

<a id="d16"></a>
## D16 — Vault key silently regenerated when missing (P2, by inspection)

**Location:** `hyveman-api/src/Hyveman.Infrastructure.Security/CredentialVault.cs:68-82`
**Contract:** API.md §11 startup step 2 ("load/**validate** the vault key");
DESIGN §9 ("Restore requires the snapshot **plus K**").

`LoadOrCreateKey` mints a fresh 32-byte key whenever the file is absent, with no
check for whether encrypted credentials already exist. `CheckKey()` — used by the
readiness probe — just forces the lazy load, so it *passes* after minting.

**Failure scenario.** A restore brings `hyveman.db` but not `vault.key` (the
exact hazard DESIGN §9 calls out), or `--data-dir` points somewhere new. The API
starts, reports ready, serves the UI — and every stored iDRAC credential and
Telegram token is now undecryptable. The failure surfaces later and indirectly,
as poll failures and silent notification failures, with no message pointing at
the real cause.

**Fix.** At startup, if the key file is absent **and** the `credentials` table is
non-empty, fail closed with a diagnostic naming the missing key path. Minting on
a genuinely fresh install stays correct.

---

<a id="d17"></a>
## D17 — Vault key file gets no ACL on Windows (P2, by inspection)

**Location:** `CredentialVault.cs:84-95`
**Contract:** API.md §10.1 — "The key file and data files are ACLed to the
service account and local administrators"; DESIGN §7.

```csharp
if (!OperatingSystem.IsWindows())
    File.SetUnixFileMode(path, UserRead | UserWrite);
```

The restriction is applied on Unix only. On Windows — the primary documented
deployment — the key file simply inherits the parent directory's ACL. INSTALL.md
§4.2 tells the operator to `New-Item -ItemType Directory -Path C:\hyveman\data`
with no ACL step, and unlike the agent installer (`install.ps1:129-139`, which
does set ACLs) there is no API installer to do it. A directory created that way
under `C:\` inherits `BUILTIN\Users: Read` by default, so any local user can
read K and decrypt every stored iDRAC and Telegram credential.

**Fix.** Set an explicit DACL (SYSTEM + Administrators, inheritance disabled) on
the key file and data directory at creation on Windows, and add the ACL step to
INSTALL.md §4.2. Both, ideally — the code should not depend on the runbook.

---

<a id="d18"></a>
## D18 — Loopback is always trusted for first-run setup (P2, by inspection)

**Location:** `hyveman-api/src/Hyveman.Api/TrustedNetwork.cs:21`
**Contract:** API.md §8.1, DESIGN §8 — first-run registration is permitted only
"from the configured localhost/trusted network".

```csharp
if (IPAddress.IsLoopback(ip)) return true; // localhost is always trusted
```

Loopback bypasses the configured CIDR list unconditionally. In the documented
topology the API listens on `127.0.0.1:5080` behind a reverse proxy, and the
real client IP arrives only via `X-Forwarded-For`. `UseForwardedHeaders` is
configured (`Program.cs:208`) so this normally works — but if the proxy is
misconfigured, or a deployment fronts the API without XFF, **every** request
presents as loopback and the first-run passkey wizard becomes available to the
internet.

The window is narrow (only while `passkeys` is empty — a fresh install or after
`auth reset`), but inside it an attacker registers the first passkey and owns the
console. Given DESIGN §8 explicitly assumes an internet-exposed UI, the
always-true shortcut converts a proxy misconfiguration into a full auth bypass.

**Fix.** Drop the unconditional branch and rely on the configured list (the
default `["127.0.0.1/32","::1/128"]` already covers local operation). Optionally
log loudly when a setup attempt arrives from loopback while `ForwardedHeaders`
found no XFF.

---

<a id="d19"></a>
## D19 — Pinned-CA mode skips hostname validation (P2, by inspection)

**Location:** `hyveman-agent/src/Hyveman.Agent/Net/BackendClient.cs:50-64`
**Contract:** PROTOCOL §2 — "the server presents a certificate valid for the
configured hostname ... The agent validates against the system store unless
`ca_path` pins a private CA".

When `ca_path` is set, the custom `RemoteCertificateValidationCallback` returns
`chain.Build(...)` against the pinned root. Supplying a callback replaces .NET's
default validation wholesale — including the SAN/hostname check. The callback
verifies the chain but never compares the certificate identity to the URL host.

Any certificate issued by the pinned CA for *any* hostname is therefore accepted
for the backend connection. On a lab/private-CA network that also issues certs
for other internal services, a holder of one of those certs plus a MITM position
can impersonate the backend and harvest agent tokens and log content.

**Fix.** Keep the pinned-root chain build, and additionally verify the hostname
— either check `sslPolicyErrors` for `RemoteCertificateNameMismatch` (the
callback receives it) or match the URI host against the certificate SANs
explicitly.

---

<a id="d20"></a>
## D20 — Login ceremony sets user verification to `Discouraged` (P2, by inspection)

**Location:** `hyveman-api/src/Hyveman.Infrastructure.Security/WebAuthnService.cs:154`
**Contract:** API.md §8.1 — the API "validates challenge, origin, RP ID, **user
verification**, credential ID, signature counter"; DESIGN §8 (single admin,
internet-exposed, no second factor).

Registration requests `UserVerificationRequirement.Preferred` (line 74) but login
requests `Discouraged`. With `Discouraged` the authenticator is told not to
perform PIN/biometric verification, so mere possession of the security key — or
of an unlocked phone with the passkey — authenticates. For an internet-exposed
console whose *only* credential is the passkey, that removes the "something you
know/are" half of the factor.

The docs do not literally say `Required`, so this is a hardening
recommendation rather than a flat contract violation — but it is inconsistent
with the registration ceremony and with §8.1 listing user verification as
something the API validates.

**Fix.** Use `Required` (or at minimum `Preferred`) for login and assert
`result.IsUserVerified` where the policy demands it. Worth confirming the
operator's authenticators support UV before switching to `Required`.

---

<a id="d21"></a>
## D21 — Health history is never downsampled (P2, by inspection)

**Location:** `hyveman-api/src/Hyveman.Application/HostsService.cs:193-233`
**Contract:** API.md §7.1 — "Separate history endpoints return chart data at a
requested time range and **server-selected resolution**. The API is responsible
for downsampling or bucketing; the browser must not download an entire
multi-year metric series." FRONTEND §8.2 mirrors it.

The `resolution` parameter is accepted, echoed back in the response
(`Resolution = resolution ?? "auto"`), and otherwise **ignored**. The method
returns up to 5000 raw snapshots plus every metric row in range, one point per
distinct timestamp. Over a 366-day range (the cap it does enforce) at a 60 s poll
interval that is on the order of half a million metric rows serialized to the
browser.

Secondary: the merge loop is O(n²) — `points.FirstOrDefault(...)` scans the
accumulated list once per metric row (line 216).

**Fix.** Bucket server-side by a resolution derived from the range (e.g. target
~500 points), aggregating max temperature / max power / worst rollup per bucket,
and return the resolution actually used. Replace the linear scan with a
dictionary keyed by bucket.

---

<a id="d22"></a>
## D22 — First post-clear event is dropped (P2, by inspection)

**Location:** `hyveman-agent/src/Hyveman.Agent/Pipeline/ChannelSubscriber.cs:187-193`
**Contract:** PROTOCOL §18.2 worked example; AGENT §1.1 ("no gaps ... surviving
... channel clear-wrap").

On detecting a RecordID regression the callback posts a reset request and
`return 0` — discarding the event that triggered detection. The subsequent
resubscribe uses `EvtSubscribeToFutureEvents`, so that event (and anything
arriving between the clear and the resubscribe) is never collected.

PROTOCOL §18.2 walks through precisely this case and expects the post-clear
event to be delivered under the new epoch (`record_id: "e1:1"`). The synthetic
`channel_reset` marker is emitted correctly, so the gap is visible after the
fact — but the event itself is gone.

**Fix.** Re-emit the triggering event with the bumped epoch after the
resubscribe (it is already rendered in hand), or accept the gap and say so
explicitly in AGENT §6.7 — but the spec's worked example should then be
corrected to match.

---

<a id="d23"></a>
## D23 — Channel reset detection dies after a retried subscribe (P2, by inspection)

**Location:** `ChannelSubscriber.cs:86-91`
**Contract:** AGENT §6.7.

If the initial `TrySubscribe` fails with anything other than
channel-not-found or a stale-bookmark error, the code awaits
`RetrySubscribeUntilSuccessAsync(...)` and then **returns from
`ExecuteAsync`**. The control loop at line 97 — the only consumer of the
`_control` channel — therefore never starts.

After such a recovery the subscription works, but any later RecordID regression
writes a reset request into an unbounded channel nobody reads: the epoch is
never bumped, the resubscribe never happens, and post-clear events collide in
the server's idempotency key and are silently deduped away (the exact failure
DESIGN §13 #15 exists to prevent).

**Fix.** Fall through to the control loop after a successful retry instead of
returning; the retry helper should signal success rather than own the lifetime.

---

<a id="d24"></a>
## D24 — Smaller items (P3, by inspection)

1. **`events_sent` double-counted.** `BatchBuilder.FlushAsync:131` adds
   `events.Count` at spool time and `LogSender:142` adds
   `accepted + deduped` again at send time. The heartbeat counter (AGENT §8) is
   roughly 2× reality. Pick one site — send time matches the field's meaning.
2. **Polly referenced but unused.** `Hyveman.Agent.csproj` declares
   `Polly 8.4.2`; AGENT §18 specifies it for retry/backoff. Retry is hand-rolled
   in `Backoff`/`LogSender` and is correct per PROTOCOL §14. Drop the package or
   amend §18 — the latter is probably right.
3. **`auth reset` does not revoke web sessions.** `AdminCommands.cs:32` clears
   `passkeys` and `webauthn_challenges` but leaves `web_sessions`, so an
   already-authenticated browser keeps admin access after a console reset. Given
   reset is the lockout/recovery path, it should revoke sessions too.
4. **Batch item-count cap not enforced agent-side.** `EnvelopeBuilder.ChunkToSize`
   splits on bytes only. The shutdown drain (`BatchBuilder:104`) can emit up to
   `in_memory_queue_events` (default 10 000) items in one batch, exceeding
   PROTOCOL §12's 1000. It self-heals via the `too_many_items` → split path, at
   the cost of several avoidable 400s. Split on item count too.
5. **Agent-silent threshold hard-coded in the UI mapper.**
   `AgentStatusMapper.ToDto:117` uses a literal 5 minutes instead of
   `HeartbeatSilenceThresholdS`, so raising the configured threshold desynchronizes
   the tile from the alert.
6. **`SqliteCacheMode.Shared` with pooling.** `SqliteDb.cs:21` enables shared
   cache. Shared cache changes locking to table-level and surfaces
   `SQLITE_LOCKED`, which `busy_timeout` does **not** retry — a documented
   pitfall with WAL and concurrent writers, and this process has five background
   services plus ingest. Not observed failing; recommend dropping to the default
   private cache.
7. **Rate-limiter keys are never evicted.** `RateLimiterRegistry._limiters`
   grows one entry per source/IP forever. Bounded-growth DoS; add expiry.
8. **CIDR parse accepts out-of-range prefixes.** `TrustedNetwork.Parse` does not
   range-check, so a config typo like `10.0.0.0/999` indexes past the address
   bytes and throws at request time. Validate `0 <= prefix <= 32/128`.
9. **Channel test records a null actor.** `ChannelsService.TestAsync:111` passes
   `actor: null` while every other mutation records the caller. API.md §7.3
   wants the authenticated actor on every action.
10. **Job Object memory cap is not a "kill".** AGENT §4.2/§19.B #8 state the OS
    "kills the agent" on exceeding `JOB_OBJECT_LIMIT_PROCESS_MEMORY`. It actually
    makes allocations fail; the process dies only because an unhandled
    `OutOfMemoryException` terminates .NET. Same practical outcome, but the test
    in §19.B should assert process exit, not a specific kill mechanism.
11. **Webhook targets may use plain `http`.** `Notifiers.cs:90` accepts
    `http` or `https`. Admin-controlled, so low risk, but a webhook URL is itself
    a secret (API.md §4.2 lists it among values never to expose) and sending it
    in the clear leaks it. Consider https-only, as the iDRAC URL validator
    already does.
12. **API.md §6.7 overstates schema-validation scope.** The validator runs on
    `/register` and `/ingest/telemetry` but not `/ingest/logs`, which is correct
    — per-item rejection (PROTOCOL §6.4) is incompatible with a whole-body check,
    and `ParseLogItem` does the equivalent per item. Add a sentence to §6.7
    recording that, and why.
13. **Frontend layout drift.** FRONTEND §4 lists `features/saved-searches/` and
    `auth/useSession.ts`; saved searches live in `features/events/` and session
    state in `AuthProvider.tsx`. §4 is labelled "recommended" — no code change
    needed, listed so it is not mistaken for a missing feature.

---

## Audit coverage

**Read in full and found conformant** (recorded so a future pass need not
re-derive it): the agent protocol middleware (version precedence, HTTPS, gzip
and the 4 MiB decompressed cap, Content-Encoding/Content-Type handling, error
envelopes, `commands: []`); token authentication and the atomic
`RegistrationUnit` (`BEGIN IMMEDIATE`, `(kind,hostname)` reuse, single-use
consumption); `ProtocolValidation` item parsing and the §6.4 rejection reasons;
`LogonStatsService` and `LogonStatsStore` (including the NULL-`logon_type`
upsert path); `AgentStatusStore` heartbeat/facts ordering; `HostsService` and
`ChannelsService` secret handling (write-only, blank-means-keep, redacted
summaries, audit on mutation); `CsrfMiddleware` and `SessionAuthHandler`;
`Migrations` against the DESIGN §5 schema; `BackupStore`'s 7/4/12 ladder;
`SpoolWriter`/`SpoolCaps`/`BatchBuilder` bookmark ordering (AGENT §6.6);
`EnvelopeBuilder` field mapping and truncation; `TelemetrySender`'s 3-attempt
budget; `JobObjectHost`; `hyveman-web`'s `client.ts`, `guards.ts` and the §5
route table.

**Read and found defective:** everything in the table above.

**Not read:** `hyveman-web` component/e2e test suites, `tools/`, the agent's
`EventRenderer`/`WevtApiNative` PInvoke layer beyond its call sites, and
`install.ps1`/`uninstall.ps1` beyond the ACL and service-creation sections.

Two of the six P1 defects were found only after writing a throwaway test; four
were found by reading code that the 125-test suite exercises and passes. A green
suite is not evidence of conformance here — the gap is in what the tests assert,
not in whether they run.
