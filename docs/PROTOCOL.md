# Hyveman Wire Protocol Specification (v1)

This is the **contract for the network boundary** between `hyveman-agent`
and `hyveman-api` (DESIGN §14 #1). Both sides are built against this
document. It is authoritative for: transport, authentication, versioning,
endpoints, request/response envelopes, the idempotency key, error semantics,
and the forward-compatible reservation for the command channel (DESIGN §12).

- **Companion docs:** `docs/DESIGN.md` (system contract), `docs/AGENT.md`
  (agent build contract). Where this doc and AGENT.md describe the same wire
  behavior, this doc is the spec; AGENT.md references it.
- **Status:** v1 — covers MVP ingest (logs + telemetry + heartbeat +
  registration + health). The command channel (DESIGN §12) is **reserved**
  here but not specified; a later command-channel spec will define it.
- **Protocol version:** `1` (see §3).

---

## 1. Scope

In scope (v1):
- Transport & security (§2)
- Protocol versioning (§3)
- Authentication & authorization (§4)
- `POST /register` — enrollment (§5)
- `POST /ingest/logs` — idempotent event batches (§6)
- `POST /ingest/telemetry` — heartbeats + facts, latest-wins (§7)
- `GET /health` — connectivity & token check (§8)
- Server-side security-logon aggregate derived from accepted curated Security items (§6.6)
- Headers reference (§9)
- Severity/facility semantics per source kind (§10)
- `record_id` / `dedup_scope` construction (§11)
- Size limits (§12), responses & errors (§13), retry/backoff (§14),
  rate limiting (§15), command channel reservation (§16)

Out of scope (separate specs, later):
- **Syslog receiver** transport (UDP/TCP 514, RFC 3164/5424) — DESIGN §11 #3,
  Phase 3. The *envelope* in this doc already accommodates syslog-originated
  events (§10, §11, App. C); only the wire transport differs.
- **Command channel** payload/signing — DESIGN §12. Reserved here (§16).
- **Web frontend / admin API** (React frontend, passkey auth) — DESIGN §8, separate.

---

## 2. Transport & security

- **Scheme:** `https://` only. Plain HTTP is rejected (lab `validate_cert=false`
  still runs over TLS — only *certificate validation* is disabled, never TLS).
- **TLS:** 1.2 minimum, 1.3 preferred. Suites negotiated by the server; the
  agent accepts the system trust store by default.
- **Server identity:** the server presents a certificate valid for the
  configured hostname (Let's Encrypt or own CA — DESIGN §8). The agent
  validates against the system store unless `ca_path` pins a private CA; if
  `validate_cert=false`, the agent skips validation (lab only, logged loudly).
- **Agent identity:** the agent authenticates with a bearer token (§4). It
  presents **no client certificate** in v1.
- **No inbound listener on the agent** (DESIGN §12 #1). The agent is strictly
  a client; the server never initiates connections.
- **No long-lived connections** in v1: one HTTPS request per batch, closed
  after the response. (A future long-poll for command pickup, DESIGN §12, may
  reuse the telemetry response — reserved, §16.)
- **Body encoding:** JSON, UTF-8. Optional gzip (§9, §12).

---

## 3. Versioning

A single integer protocol version, currently **`1`**, is the version of *this*
whole wire protocol (transport, endpoints, headers, auth, envelope, responses).

- It appears on **every** request and response as:
  - header `X-Hyveman-Protocol: <v>`, and
  - a top-level `"v": <v>` field in every JSON body (request and response).
- Both **must** carry the same value on a given exchange.
- **Version-mismatch responses (exception):** a client using an unsupported
  version cannot be answered in that version. Responses to requests with a
  missing, unsupported, or header/body-mismatched version therefore carry the
  **server's** current version in `X-Hyveman-Protocol` and `v`, with
  `error.supported` listing the versions the server serves (and/or
  `missing_version` for the absent-header case).
- **Validation precedence (server side), in order:** (1) `X-Hyveman-Protocol`
  absent → `400 missing_version`; (2) header version unsupported → `400
  unsupported_version` with `error.supported`; (3) body `v` present but ≠
  header → `400 invalid_request`. A body `v` alone never substitutes for the
  header.
- **Bump rules:**
  - A **major** change (new *required* field, removed field, changed
    semantics, new endpoint that changes the contract) bumps `v`.
  - **Additive optional** fields (new optional body fields, new `fields.*`
    keys, new non-fatal response keys) do **not** bump `v`. Clients MUST
    ignore unknown fields (forward compatibility).
- **Server** supports a contiguous range; an unsupported major returns
  `400 unsupported_version` with `error.supported: [1]` (§13).
- **`X-Hyveman-Protocol` is required** on all requests; absence → `400
  missing_version`.

---

## 4. Authentication & authorization

### 4.1 Token model
- Every endpoint requires `Authorization: Bearer <token>` **except** `POST /register`
  (which itself uses a registration token, below) and `GET /health` without a
  token (connectivity-only, §8).
- Tokens are opaque strings, **prefixed by kind** for human readability:
  | Prefix | Kind | Scope | Lifetime | Source |
  |---|---|---|---|---|
  | `reg_...` | registration / install token | `register` | single-use | admin-issued in web UI; one per host enrollment |
  | `agt_...` | agent ingest token | `ingest` | long-lived, revocable | minted by `POST /register` |
  | (future) `cmd_...` | command-pickup token | `command-pickup` | — | DESIGN §12; not in v1 |
- Server stores **only the hash** of each token (DESIGN §5.1 `tokens.token_hash`);
  the wire carries the raw token. Tokens are **revocable** (revoked flag, §13).
- A token resolves server-side to exactly one **source** (`sources` row, DESIGN
  §5.1) and a set of scopes. The **authoritative identity is the token**; the
  request body's `source` field is **corroborating only and MUST be ignored
  for identity** (§4.2). This means a compromised agent cannot claim another
  source's identity.

### 4.2 Source identity rule (security-critical)
- `source_id` for all ingest is **derived from the token**, never trusted from
  the body. The agent includes `source` in the batch body as a hint; the server
  logs a warning if body `source` ≠ token's `source_id` (possible misconfig)
  but proceeds with the token's identity.
- Every log item's idempotency key is therefore `(token_source_id,
  dedup_scope, record_id)` (§11). The agent does not send `source_id` per item.

### 4.3 Scopes
- `register` — may call `/register` only.
- `ingest` — may call `/ingest/logs` and `/ingest/telemetry`, and `GET /health`
  with token introspection.
- `command-pickup` — future (DESIGN §12); not enforced in v1.
- Using a token on an endpoint its scope doesn't permit → `403 forbidden`
  (`wrong_scope`), not `401`.

### 4.4 Token lifecycle
- Minted by `/register` (§5). Long-lived; rotation is admin-driven (revoke +
  re-register). `last_used` is updated by the server on each authenticated
  call (for hygiene/alerting on stale tokens).
- Revoked tokens → `401 unauthorized` (`token_revoked`). Unknown/malformed
  tokens → `401 unauthorized` (`token_invalid`).

---

## 5. `POST /register` — enrollment

Resolves AGENT §20 #3. An enrollment token (`reg_...`, admin-issued, single-use)
is exchanged for a long-lived ingest-scope token + the agent's `source_id`.

### 5.1 Request
```
POST /register
Authorization: Bearer reg_<one-time-install-token>
X-Hyveman-Protocol: 1
Content-Type: application/json; charset=utf-8
```
```jsonc
{
  "v": 1,
  "kind": "windows-agent",        // requested source kind; must match the reg token's bound kind
  "hostname": "HOST01",            // becomes sources.name (must be unique per kind)
  "agent_version": "0.1.0",        // informational
  "os_build": "17763",            // informational
  "boot_id": "..."                 // optional; opaque *per-boot* identifier — changes on every
                                     // boot; informational only, NEVER used for source resolution (§5.2)
}
```

### 5.2 Server behavior
1. Validate the `reg_` token: exists, not consumed, not expired, not revoked;
   its bound `kind` matches the body `kind`.
2. **Source resolution (reinstall-friendly) — identity rule:**
   - **`(kind, hostname)` is authoritative in v1.** If a `sources` row with
     `(kind, name=hostname)` already exists → **reuse** it (this is the
     reinstall path: same host, fresh ingest token). Else create a new
     `sources` row (`kind`, `name=hostname`).
   - **Known v1 limitation (accepted):** two *distinct* physical hosts that
     share a hostname resolve to the same source. `boot_id` is deliberately
     **not** part of source resolution — it changes on every boot and cannot
     distinguish hosts; the operator renames the source in the UI when this
     is discovered. A future revision may add a stable machine identity and
     true collision disambiguation (the `409 name_collision` code is
     reserved for that, §5.4).
3. Mint a new `agt_...` token → insert `tokens` row (`source_id`,
   `token_hash=hash(token)`, `scopes=[ingest]`, `created=now`).
4. Mark the `reg_` token **consumed** (single-use). Consumed tokens cannot be
   reused → `410 token_consumed`.
5. Return 200 with the token + source_id.

### 5.3 Response — 200
```jsonc
{
  "v": 1,
  "source_id": "src_01HW...",
  "token": "agt_01HW...",
  "scopes": ["ingest"],
  "issued_at": "2024-08-07T15:02:11Z",
  "commands": []                // reserved (§16); always [] in v1
}
```
The agent writes `token` (and `source_id` for telemetry corroboration) into
`agent.json` (ACL'd, AGENT §13) and **discards the `reg_` token** (AGENT §11.2
step 9). Reinstall on the same host requires a freshly admin-issued `reg_` token
(§5.2 reuse path keeps the same `source_id`).

### 5.4 Errors
| Status | `error.code` | When |
|---|---|---|
| 400 | `unsupported_version` | protocol version not served (see §3) |
| 400 | `invalid_request` | malformed JSON / missing required field |
| 401 | `token_invalid` | unknown / malformed `reg_` token |
| 401 | `token_revoked` | `reg_` token revoked |
| 410 | `token_consumed` | `reg_` token already used (reissue via admin UI) |
| 409 | `name_collision` | reserved — unused in v1: with `(kind, hostname)` authoritative (§5.2), registration either reuses or creates; kept for a future machine-identity scheme |
| 429 | `too_many_requests` | rate-limited (§15); `Retry-After` set |

**Response-loss recovery:** if the connection fails after the server consumed
the `reg_` token but before the agent received the response, the agent has no
token and a retry receives `410 token_consumed`. The agent fails closed (does
not start; clear diagnostic). Recovery: reissue a fresh `reg_` token in the
admin UI and restart — the §5.2 reuse path keeps the same `source_id`. (A
future revision may add an idempotent registration/recovery flow; see
PROTOCOL_REVIEW §3.)

---

## 6. `POST /ingest/logs` — idempotent event batches

The durable, idempotent ingest path. The agent spools batches to disk and
retries; the server deduplicates on the idempotency key (DESIGN §13 #11/#15,
§13 #16).

### 6.1 Request
```
POST /ingest/logs
Authorization: Bearer agt_<token>
X-Hyveman-Protocol: 1
X-Hyveman-Source: src_01HW...          # corroborating; authoritative = token (§4.2)
Content-Type: application/json; charset=utf-8
Content-Encoding: gzip                 # optional (§9); omit for identity
```
```jsonc
{
  "v": 1,
  "source": "src_01HW...",             // corroborating only (§4.2); server uses token
  "items": [
    {
      "kind": "log",
      "record_id": "41235",            // §11: bare, or "e<epoch>:<id>" after a channel clear
      "dedup_scope": "System",         // §11: channel name (Windows); "" for syslog seq
      "time": "2024-08-07T15:02:11.123Z",  // UTC ISO-8601, event TimeCreated
      "severity": 3,                  // §10: Windows Level (1=Critical..5=Verbose)
      "facility": "Microsoft-Windows-Kernel-Power",  // §10: = provider name (NOT channel)
      "message": "The system ...",     // rendered user-facing text
      "fields": {
        "channel": "System",          // → events.channel (promoted indexed column)
        "event_id": 6008,              // → events.event_id
        "task": 0,                     // → events.task
        "opcode": 0,                   // → events.opcode
        "keywords": "0x80000000000000",// → events.keywords (hex string)
        "provider_guid": "{...}",      // correlation, stays in fields_json
        "computer": "HOST01",
        "activity_id": "...",
        "process_id": 0,
        "thread_id": 0,
        "event_data": {                // parsed EventData Name/Value (Windows); arbitrary for syslog
          "LogonType": 10,
          "TargetUserName": "admin"
        }
      },
      "raw": "<Event xmlns='...'>...</Event>"  // original XML (Windows); capped (§12)
    }
  ]
}
```
- **`items`** are homogeneous: `/ingest/logs` accepts **only** `kind:"log"`.
  Other kinds → `400 invalid_request` (`wrong_item_kind`).
- **Within-channel order** is the agent's responsibility (AGENT §16); the
  agent preserves it across the batch. Cross-channel order is irrelevant
  (server indexes by `time`).

### 6.2 Server-side ingest mapping (for server implementers)
The item maps onto the `events` table (DESIGN §5.1):
| Envelope field / path | `events` column | Notes |
|---|---|---|
| (token → source_id) | `source_id` | **not** the body `source` |
| `dedup_scope` | `dedup_scope` | NON-NULL, default `''` |
| `record_id` | `record_id` | TEXT, opaque; part of UNIQUE key |
| `time` | `time` | UTC |
| `severity` | `severity` | per-kind scale (§10) |
| `facility` | `facility` | provider name (Windows) |
| `message` | `message` | rendered text |
| `fields` (whole object) | `fields_json` | |
| `raw` | `raw_json` | truncated-then-sent OK |
| `fields.channel` | `channel` | promoted (Windows) |
| `fields.event_id` | `event_id` | promoted (Windows) |
| `fields.task` | `task` | promoted (Windows) |
| `fields.opcode` | `opcode` | promoted (Windows) |
| `fields.keywords` | `keywords` | promoted (Windows, hex string) |
- **Idempotent insert:** `INSERT ... ON CONFLICT(source_id, dedup_scope, record_id) DO NOTHING`
  (or equivalent). Conflicts → counted in `deduped` (§6.3), not errors.
- A whole batch is processed item-by-item; one bad item does **not** reject the
  batch — per-item results are returned (§6.3).

### 6.3 Response — 200
```jsonc
{
  "v": 1,
  "accepted": 12,          // count newly stored
  "deduped": 0,            // count already present (collapsed by UNIQUE key)
  "rejected": [            // per-item rejections (server-refused, NOT retried)
    { "record_id": "999", "dedup_scope": "System",
      "reason": "raw_oversize", "permanent": true }
  ],
  "commands": []           // §16: reserved; always [] in v1
}
```
- `accepted + deduped + len(rejected) == len(items)` (server invariant).
- `rejected[].permanent=true` → the agent **quarantines** the item's batch
  (AGENT §6.5) and does NOT retry. `permanent=false` is not used in v1
  (non-permanent failures are communicated via 5xx for the whole batch).
- `commands` is reserved (§16); v1 agents ignore non-empty values.

### 6.4 Per-item rejection reasons (in `rejected[].reason`)
| `reason` | `permanent` | Meaning |
|---|---|---|
| `raw_oversize` | true | `raw` exceeded the server hard cap (§12) |
| `message_oversize` | true | `message` exceeded limit |
| `field_oversize` | true | a `fields` value exceeded limit |
| `bad_time` | true | `time` missing / unparseable / not UTC ISO-8601 |
| `bad_record_id` | true | `record_id` missing / empty / > 128 chars |
| `bad_dedup_scope` | true | `dedup_scope` missing (null) — must be `""` if empty (§11) |
| `schema` | true | item otherwise malformed |

### 6.5 Whole-batch errors (non-2xx)
See §13. On 4xx non-retryable, the agent quarantines the batch — **except**
`400 too_many_items` / `413 payload_too_large`, which split & resend (§14);
on 5xx/408/429 it keeps the spool file and retries (AGENT §6.5).

### 6.6 Server-side derived aggregate — security-logon stats

The server derives per-user/per-day security-logon counts (`logon_stats`,
DESIGN §4.1/§13 #5) from **accepted** `/ingest/logs` items. This is
server-owned derived data and never appears on the wire to agents, but it
makes the following agent-shipped content load-bearing:

- `fields.channel` must be `Security` (compared case-insensitively).
- The curated event IDs, using `fields.event_data`:

  | Event ID | Meaning | Counted as |
  |---|---|---|
  | 4624 | successful logon — **only** `event_data.LogonType` `2` (interactive) or `10` (RDP) | success |
  | 4625 | failed logon, all logon types | failure |
  | 4740 | account lockout (carries no logon type) | failure |

- `fields.event_data.TargetUserName` (string, non-empty) must be present for
  an item to aggregate; the day is the UTC calendar day (`yyyy-MM-dd`) of
  `time`.

Semantics:

- Only **newly accepted** items count; deduped replays (§6.2) never
  double-count.
- The server applies its own curation to whatever arrives; the agent's
  Security filter (DESIGN §4.1) and the server-side policy are independent.
- An aggregation failure never rejects or affects the already-committed batch
  (derived data).
- The aggregate is read-only to agents and is exposed to the frontend through
  the web admin API (API.md §7.5).

---

## 7. `POST /ingest/telemetry` — heartbeats + facts (latest-wins)

Best-effort, **not idempotent**, **not spooled** (a missed heartbeat *is* the
alert signal — replaying old ones is wrong; DESIGN §13 #16). Items: `heartbeat`
and `facts`. A batch may carry one of each, or multiple facts snapshots; the
server stores latest-wins per item kind.

### 7.1 Request
```
POST /ingest/telemetry
Authorization: Bearer agt_<token>
X-Hyveman-Protocol: 1
X-Hyveman-Source: src_01HW...
Content-Type: application/json; charset=utf-8
```
```jsonc
{
  "v": 1,
  "source": "src_01HW...",
  "items": [
    {
      "kind": "heartbeat",
      "source_id": "src_01HW...",       // corroborating only (§4.2); additive-optional (§3)
      "sent_at": "2024-08-07T15:02:11Z",
      "agent_version": "0.1.0",
      "protocol_version": 1,
      "os_build": "17763",
      "boot_time": "2024-08-01T00:00:00Z",
      "uptime_s": 123456,
      "mem_total_bytes": 34359738368,
      "mem_available_bytes": 8589934592,   // additive-optional; Windows available (free + standby)
      "free_disk": [
        { "path": "C:\\", "bytes": 12345678, "pct": 0.23 }
      ],
      "counters": {
        "events_sent": 1000, "events_dropped": 0,
        "batches_sent": 5, "batches_failed": 0,
        "spool_bytes": 0, "spool_files": 0,
        "queue_depth": 0, "wmi_timeouts": 0,
        "send_errors_last_min": 0
      },
      "degraded": "",                    // "" | "spool_full" | "overrun" |
                                         // "auth_rejected" | "quarantined" |
                                         // "wmi_degraded" | "channel_reset"
      "config_hash": "a1b2c3"            // short hash of active config
    },
    {
      "kind": "facts",
      "collected_at": "2024-08-07T15:02:10Z",
      "stale": false,                    // true ⇒ prior facts re-emitted after a WMI timeout
      "vms": [
        { "name": "VM1", "state": "on", "heartbeat_ok": true,
          "cpu_pct": 12.3, "mem_mb": 4096,
          "last_seen": "2024-08-07T15:02:09Z" }
      ]
    }
  ]
}
```
- **`kind:"heartbeat"` fields** mirror AGENT §8. `counters` are monotonic-ish
  per-interval (agent may send cumulative counters; the server diffs for
  rates). All fields are informational for the server; the server uses
  `sent_at`, `degraded`, `counters` for alerting (DESIGN §4.4 heartbeat rules).
  `mem_total_bytes`/`mem_available_bytes` are **additive-optional** (§3): an
  agent that omits them simply contributes no RAM metrics that interval. The
  item may additionally carry `source_id` as corroboration only —
  identity always comes from the token (§4.2).
- **`kind:"facts"` `vms[].state`** ∈ `on|off|paused|saved|other|unknown`;
  `heartbeat_ok` ∈ `true|false|null`. `"vms": []` with `stale:false` means the
  host has **no VMs** (scan succeeded); a failed scan never yields an empty
  list — prior facts are re-emitted with `stale:true` (§7.4).
- No `record_id`/`dedup_scope` on telemetry items — not idempotent.

### 7.2 Server behavior
- Overwrites/stores the latest heartbeat per source (latest-wins, §7.4). Stores
  facts into `vms` (DESIGN §5.2) and `health_snapshots`-adjacent state.
- Heartbeat arrival resets the "agent silent" timer (DESIGN §4.4 rule type 3)
  — based on the server's **receive time** (`received_at`), never on `sent_at`
  (§7.4).

### 7.3 Response — 200
```jsonc
{ "v": 1, "accepted": true, "commands": [] }
```
- `accepted` is per-batch (true if every item parsed; rejected items would make
  this 4xx — see §13). Telemetry does NOT return per-item sub-results; a
  malformed heartbeat is rare and fatal to the batch (4xx), discarded (resends
  next interval).
- `commands` reserved (§16).

### 7.4 Ordering rule (what "latest-wins" means)
- **Heartbeat:** stored state is replaced iff the incoming item has a
  *different* `boot_time` (a new boot session) **or** a `sent_at` newer than
  the stored heartbeat's `sent_at`. An older `sent_at` with the same
  `boot_time` is ignored — a reordered or retried request must not regress
  state. The server records `received_at` independently of `sent_at`.
- **Facts:** replaced iff `collected_at` is newer than the stored snapshot's
  `collected_at`; otherwise ignored. Multiple `facts` items in one request are
  applied in array order under the same rule.
- **Stale snapshots:** a `stale:true` facts item replaces stored state the
  same way (its `collected_at` is newer), but the server SHOULD flag it as
  stale in the UI. An empty `vms` list is *not* a failure signal (§7.1).
- **Response for a valid-but-older payload:** still `200 { "v": 1,
  "accepted": true, "commands": [] }` — there is nothing to signal and the
  next interval resends fresh state anyway. Telemetry never returns per-item
  results.

---

## 8. `GET /health` — connectivity & token check

Lightweight, used by installer preflight (AGENT §11.3) and agent startup.

```
GET /health
X-Hyveman-Protocol: 1
Authorization: Bearer agt_<token>    # OPTIONAL
```

### 8.1 Behavior
- **No `Authorization`** → connectivity check only.
- **With `Authorization`** → also introspects the token.

### 8.2 Response — 200
```jsonc
{
  "v": 1,
  "ok": true,
  "server_time": "2024-08-07T15:02:11Z",
  "server_version": "0.1.0",
  "source_id": "src_01HW...",      // present only if Authorization resolved
  "scopes": ["ingest"],            // present only if Authorization resolved
  "commands": []                    // reserved (§16); always [] in v1
}
```
- Always 200 if the server is reachable & healthy, **regardless of token** (so
  preflight can confirm reachability). Token validity surfaces via the presence
  of `source_id`/`scopes`: absent → token was invalid/missing; present → valid.
  (A stricter variant returning 401 on a bad token is allowed but not required;
  the agent keys off `.ok` for connectivity and `.source_id` for token validity.)
- `503` if the server is up but not ready (booting, DB locked beyond timeout).

---

## 9. Headers reference

### 9.1 Request headers
| Header | Required | Value | Notes |
|---|---|---|---|
| `Authorization` | yes (except `GET /health` w/o token) | `Bearer <token>` | §4 |
| `X-Hyveman-Protocol` | yes | `<int>` | §3; absent → `400 missing_version` |
| `Content-Type` | yes (when body) | `application/json; charset=utf-8` | |
| `X-Hyveman-Source` | no | `<source_id>` | corroborating only (§4.2); ingest endpoints only — omitted where no source is known (e.g. `/register`) |
| `Content-Encoding` | no | `gzip` | omit ⇒ identity; §12 |
| `Accept` | recommended | `application/json` | server only returns JSON |
| `User-Agent` | recommended | `hyveman-agent/<ver> (+<kind>; os=<build>)` | hygiene |

### 9.2 Response headers
| Header | Value | Notes |
|---|---|---|
| `X-Hyveman-Protocol` | `<int>` | echoed |
| `Content-Type` | `application/json; charset=utf-8` | |
| `Retry-After` | `<seconds>` | on `429`/`503` (§14) |
| `X-RateLimit-Remaining` | optional | if server exposes per-source budget (§15) |

---

## 10. Severity & facility semantics (per source kind)

The envelope carries native `severity`/`facility` **whose scale is determined
by the source kind** (known server-side from the token → `sources.kind`).
There is intentionally **no `severity_scale` field** — the kind disambiguates
(DESIGN §5.1, §11). Cross-kind alert rules consult the source kind.

| Source kind | `severity` | `facility` | Notes |
|---|---|---|---|
| `windows-agent` | Windows Level: `1`=Critical,`2`=Error,`3`=Warning,`4`=Information,`5`=Verbose (int) | provider name (string), e.g. `Microsoft-Windows-Kernel-Power` | **NOT** the channel; channel is in `fields.channel` |
| `linux-agent` (future) | (defined when native agent lands — likely syslog 0–7 mapped or a native scheme) | (likely syslog facility) | TBD with the Linux agent (DESIGN §11 #4) |
| `syslog-feed` (Phase 3) | RFC 5424 severity `0`–`7` (int) | RFC 5424 facility — numeric as a decimal string (e.g. `"3"`) or its standard name (e.g. `"daemon"`); **server treats as opaque string** | `dedup_scope` is `""`, `record_id` is a per-source sequence (§11) |

> **Windows Levels** exactly mirror the `Level` enum in the rendered event
> (Critical/Error/Warning/Information/Verbose). The agent forwards the integer
> unchanged; the server does **not** re-map at ingest (it stores and indexes
> as-is). Alert rules that compare severities must use the Windows scale for
> `windows-agent` sources.
>
> An event whose `Level` is unspecified (0) is sent with `severity` **omitted**
> (absent, not 0); the server should default such rows (e.g. to Information)
> at ingest.

---

## 11. `record_id` & `dedup_scope` construction (idempotency)

The server uniqueness key is `(source_id, dedup_scope, record_id)` (DESIGN
§13 #11). `dedup_scope` is `TEXT NOT NULL DEFAULT ''`; `record_id` is `TEXT`
(opaque to the server, ≤ 128 chars).

### 11.1 Windows agent
- `dedup_scope` = **channel name** (e.g. `"System"`,
  `"Microsoft-Windows-Hyper-V-VMMS-Admin"`). Always non-empty. Exception:
  synthetic/self-collect entries that map onto a *shared* channel via a
  provider filter (e.g. the agent's own `"HyvemanAgent"` lifecycle source,
  which reads from `Application`) use the **config entry name** as
  `dedup_scope`, so their EventRecordIDs cannot collide with the shared
  channel's records in the UNIQUE key.
- `record_id` = **EventRecordID**, with an epoch prefix after a channel clear
  (DESIGN §13 #15):
  - Normal (epoch 0): `record_id = "<EventRecordID>"` (bare decimal),
    e.g. `"41235"`.
  - After a detected reset: `record_id = "e<epoch>:<EventRecordID>"`, e.g.
    `"e1:1"`, where `<epoch>` starts at `1` and increments on each subsequent
    reset for that channel.
- **Reset detection** (agent-side): an `EvtSubscribe(StartAfterBookmark)`
  returning a stale/invalid-bookmark error, or observing a RecordID **lower
  than the channel's persisted max** ⇒ bump epoch, resubscribe from "now",
  emit a synthetic `channel_reset` event, set `degraded="channel_reset"`
  (AGENT §6.7).
- **Server contract:** `"41235"` and `"e1:41235"` and `"e2:41235"` are **distinct**
  opaque strings → distinct rows. Within an epoch, replays collapse.
- Why epoch not timestamp: RecordID is the only thing the bookmark tracks; the
  epoch is a single integer persisted per channel (`state\<channel>.epoc`),
  crash-safe via atomic temp+rename (AGENT §6.7).

### 11.2 Syslog feed (Phase 3)
- `dedup_scope` = `""` (empty, the default).
- `record_id` = a per-source **monotonic sequence** assigned by the syslog
  receiver (decimal string, e.g. `"7"`). The sequence is assigned at ingest,
  so the receiver must assign-before-store (design TBD with the syslog spec).

### 11.3 Linux agent (future)
- TBD with the agent (DESIGN §11 #4); expected to mirror either the Windows
  channel+record scheme (journald cursor) or a per-source sequence.

### 11.4 General rules
- `record_id` MUST be present, non-empty, ≤ 128 chars → else `bad_record_id`
  (§6.4).
- `dedup_scope` MUST be present; if it would be empty it MUST be the string
  `""` (never null) → else `bad_dedup_scope` (§6.4).
- The server does not parse `record_id`; it is a compare key only.

---

## 12. Size limits

| Limit | Default | Where enforced | On violation |
|---|---|---|---|
| Request body (`Content-Length`) | **4 MiB** (`max_batch_bytes`) | server | `413 payload_too_large` — agent must split & resend |
| Items per batch | **1000** | server | `400` (`too_many_items`) |
| `raw` field (per item) | **8 KiB** agent truncates to this; **16 KiB** server hard cap | both | agent truncates with marker; server `raw_oversize` reject (permanently) |
| `message` field | **64 KiB** | server | `message_oversize` reject |
| Any `fields.*` value (string) | **64 KiB** | server | `field_oversize` reject |
| `record_id` | ≤ 128 chars | server | `bad_record_id` reject |

**Limit semantics:**
- All limits are on the **decompressed (identity) JSON**. `Content-Encoding:
  gzip` only reduces bytes on the wire; it never raises or lowers a limit.
- The 4 MiB cap applies to the reassembled body for **chunked** requests too;
  the v1 agent never sends chunked bodies (always `Content-Length`).
- `Content-Encoding` other than `identity`/`gzip` → `400 invalid_request`
  (`415 unsupported_media_type` also acceptable, §13.3). The v1 agent sends
  only identity or gzip.
- Agent-side truncation of `raw` (AGENT §9.3): truncate to 8 KiB and append
  marker `…hyveman-truncated:<n>` so the server still receives a valid item.
  The rendered `message` is never truncated.
- Gzip (optional, default on for logs): `Content-Encoding: gzip` compresses an
  already-within-limit body; it doesn't change the uncompressed size limits.

---

## 13. Responses & error codes

### 13.1 Success envelope (all 2xx)
See §5.3 (register), §6.3 (logs), §7.3 (telemetry), §8.2 (health). Common
shape: `{ "v": 1, ..., "commands": [] }`.

### 13.2 Error envelope (all non-2xx)
```jsonc
{
  "v": 1,
  "error": {
    "code": "unauthorized",       // stable machine code; see tables
    "message": "human-readable diagnostic (may evolve; do not parse)",
    "supported": [1]              // present only on unsupported_version
  },
  "commands": []                  // reserved (§16)
}
```
- Clients branch on `error.code` (stable) + HTTP status, never on `message`.

### 13.3 Status codes & codes

| HTTP | `error.code` | Retry? | Meaning / agent action |
|---|---|---|---|
| 200 | — | — | success |
| 400 | `unsupported_version` | no | client version not served; check `error.supported` |
| 400 | `missing_version` | no | `X-Hyveman-Protocol` absent |
| 400 | `invalid_request` | no | malformed JSON / missing required field / wrong item kind |
| 400 | `too_many_items` | no | items > 1000 (split the batch) |
| 401 | `token_invalid` | no | unknown/malformed token (don't retry; surface to admin) |
| 401 | `token_revoked` | no | token revoked (don't retry; re-register) |
| 401 | `token_missing` | no | no `Authorization` on a required-auth endpoint |
| 403 | `wrong_scope` | no | token valid but scope insufficient (e.g. `register` on `/ingest`) |
| 404 | `unknown_source` | no | token's source_id no longer exists (deleted) — re-register |
| 410 | `token_consumed` | no | `reg_` token already used (reissue) |
| 413 | `payload_too_large` | no — **special case**: split & resend (bounded halving, §14) | body > 4 MiB (decompressed, §12) |
| 409 | `name_collision` | no | reserved — unused in v1 (§5.2/§5.4) |
| 429 | `too_many_requests` | **yes** (honor `Retry-After`) | rate-limited (§15) |
| 408 | `request_timeout` | **yes** | request timed out |
| 500 | `internal` | yes | server bug |
| 502 | `bad_gateway` | **yes** | bad gateway / proxy |
| 503 | `unavailable` | **yes** (honor `Retry-After`) | server not ready |
| 504 | `gateway_timeout` | **yes** | upstream timeout |
| network err | — | **yes** | TLS/DNS/conn reset |
| 415 | `unsupported_media_type` | no | unsupported `Content-Encoding` (§12); never sent by the v1 agent |

- **4xx-other** (not in table) → treated as non-retryable; agent quarantines
  (logs) and surfaces in heartbeat.
- **Proxy-generated errors:** 408/502/503/504 emitted by a reverse proxy may
  arrive without the JSON envelope or with a non-standard `error.code`.
  Agents MUST classify retryability from the HTTP status alone — they do
  (§14); the codes above are what a hyveman-api server emits.
- **`error.code` values are additive:** servers may introduce new codes
  without a version bump (§3); agents treat unknown codes by HTTP status class
  (§14).
- **Credential-class errors** (`token_invalid`, `token_revoked`,
  `wrong_scope`, `unknown_source`, `token_consumed`) are the exception to
  quarantine: the batch is valid, so the agent keeps the spool file, retries
  slowly (5 min), sets `degraded="auth_rejected"`, and surfaces it for
  re-registration (AGENT §6.5).
- **5xx + 408 + 429 + network/TLS** → retry per §14; logs stay in the spool.

### 13.4 Idempotency guarantees
- `/ingest/logs` is safe to retry freely: identical `(source_id, dedup_scope,
  record_id)` collapses server-side. Retries after a 5xx before the server
  committed → first commit wins; a *duplicate* retry after a successful commit
  → `deduped` count. The agent cannot tell and doesn't need to.
- `/ingest/telemetry` is **not** idempotent; retries are effectively harmless
  because latest-wins (§7.4), but old heartbeats should NOT be replayed (the
  agent never spools them — AGENT §7.1/§8).

### 13.5 Machine-readable schema
All v1 request/response JSON bodies are also specified as JSON Schema
(draft-07) at `docs/schemas/protocol-v1.json`. It mirrors this document and is
kept in lockstep with it; **this document is authoritative** on any divergence.
Server implementers SHOULD validate requests against the schema (bodies only —
headers are outside JSON Schema's scope).

---

## 14. Retry & backoff (agent side)

Applies to **logs** (spooled, retried) and **telemetry** (best-effort):

- Retriable: `408`, `429`, `5xx`, network/TLS errors.
- Non-retriable: `2xx`, `4xx` (except `408/429`). On non-retriable 4xx for
  logs → quarantine; for telemetry → discard.
- **Backoff:** exponential, base `1s`, factor `2`, per-attempt cap `60s`,
  + ±20% jitter per attempt.
- **Retry limits (per stream):**
  - **Logs: unbounded.** A log batch is retried indefinitely while its spool
    file exists (per-attempt delay is capped at `60s`; the spool honors its
    own disk caps), so a permanently-down backend can neither spin the host
    nor fill the disk — logs remain safely in the spool; the sender just
    keeps the queue.
  - **Telemetry: at most 3 attempts per interval**, then the payload is
    discarded (the next interval resends fresh state). Non-retriable
    outcomes (4xx except `408/429`) are discarded immediately, without
    burning attempts.
- **`Retry-After`** (seconds, on `429`/`503`) is honored in place of the
  computed backoff, **capped at `3600s` per wait** — a server suggesting a
  huge delay must not stall the drain indefinitely.
- **Bounded concurrency:** `send_concurrency=2` for logs (AGENT §6.5). No retry
  storm: while the backend is down the sender sleeps on the backoff, CPU ~0
  (hazard H5 in AGENT §3).
- **No partial-batch retry structure:** logs retry the *whole* batch file
  (one spool file = one batch = one POST). Server per-item rejects (§6.4) are
  final for those items; the agent does not re-split.
- **Split special case (overrides the no-resplit rule):** `413
  payload_too_large` and `400 too_many_items` do **not** quarantine. The
  agent re-chunks the spooled batch by **recursive halving** until every part
  is within limits; a single over-size item (the halving floor) is still sent
  and the server per-item-rejects it if needed (§6.4). Only these two errors
  ever trigger re-splitting.

---

## 15. Rate limiting (server side)

- **Per-source budget + global budget** (DESIGN §7 "rate-limits"). Per-source is
  load-bearing: one misbehaving agent must not starve the others.
- Over-budget → `429 too_many_requests` with `Retry-After` (seconds).
- The server SHOULD return `X-RateLimit-Remaining` (optional) so the agent can
  self-throttle.
- Aggressive limits are a **defense** (rogue/compromised agent), not a normal
  operating condition — a healthy MVP agent sends at most ~1 batch/s and a
  telemetry POST every 30 s.

---

## 16. Command channel (reserved — DESIGN §12)

The **command channel is deferred** (DESIGN §12) but the protocol reserves a
slot now so it can be added without a breaking change.

- Every 2xx and error response body **MUST include `"commands": []`**. In v1
  the server always returns `[]`; v1 agents **MUST** tolerate a non-empty array
  by ignoring items they don't understand (forward compatibility), but **MUST
  NOT** execute any command — command handling is not implemented in v1.
- Direction stays **agent-initiated** (DESIGN §12 #1): commands flow *back* on
  the agent's existing POST response (initially the heartbeat/telemetry
  response; possibly a dedicated long-poll endpoint later).
- The **`CommandRef` shape, signing/nonce scheme, agent-side allowlist, and
  execution semantics** are defined in a future **command-channel spec**
  (DESIGN §12 #4). Sketch (subject to change, do not build against):
```jsonc
"commands": [
  { "id": "cmd_01HW...", "type": "config_push", "params": { },
    "issued_at": "...", "expires_at": "...", "nonce": "...", "signature": "..." }
]
```
- v1 tokens are scope-capable for `ingest` only (DESIGN §13 #12); command
  pickup will use a `command-pickup` scope created later — no protocol change
  needed (§4.3).

---

## 17. Field mapping — Windows event → envelope (summary)

Full mapping lives in AGENT Appendix A; reproduced here as the wire contract.
All Windows-specific columns promoted to `events` indexed columns are sourced
from `fields.*`. Other `fields.*` keys stay in `fields_json`.

| Envelope | Windows source |
|---|---|
| `record_id` | `EventRecordID` (epoch-prefixed post-clear, §11.1) |
| `dedup_scope` | channel name |
| `time` | `TimeCreated` → UTC ISO-8601 |
| `severity` | `Level` (1–5) |
| `facility` | provider name |
| `message` | `EvtFormatMessage(EVT_FORMAT_MESSAGE_MESSAGE)` |
| `fields.channel` | channel name → `events.channel` |
| `fields.event_id` | `EventID` → `events.event_id` |
| `fields.task` | `Task` → `events.task` |
| `fields.opcode` | `Opcode` → `events.opcode` |
| `fields.keywords` | `Keywords` (hex string) → `events.keywords` |
| `fields.provider_guid` | `Provider.Guid` |
| `fields.computer` | `Computer` |
| `fields.activity_id` | `ActivityId` |
| `fields.process_id` / `fields.thread_id` | `Execution.ProcessID/ThreadID` |
| `fields.event_data` | parsed `EventData` `Data{Name=..}` kv |
| `raw` | `EvtFormatMessage(EVT_FORMAT_MESSAGE_XML)` (capped, §12) |

## Appendix C — Syslog → envelope mapping (Phase 3, provisional)

When the syslog receiver ingests (DESIGN §11 #3), it maps onto the same
envelope. Provisional (finalized with the syslog spec):

| Envelope | syslog (RFC 5424) |
|---|---|
| `record_id` | per-source sequence (assigned by receiver) |
| `dedup_scope` | `""` |
| `time` | `TIMESTAMP` → UTC ISO-8601 |
| `severity` |RFC 5424 `SEVERITY` (0–7) |
| `facility` | RFC 5424 `FACILITY` (numeric-as-string or standard name) |
| `message` | `MSG` (`%msg%`) |
| `fields.*` | `STRUCTURED-DATA` (SD-ID/SD-PARAM → kv) + hostname/appname/procid |
| `raw` | the raw RFC 5424 line (capped) |

---

## 18. Worked examples

### 18.1 First-run registration → first heartbeat → first log batch
1. Admin issues `reg_01ABC` bound to `kind=windows-agent` in the web UI.
2. Agent `POST /register` with `reg_01ABC` → gets
   `{source_id:"src_01HW", token:"agt_02HW", scopes:["ingest"]}`. Stores token,
   discards `reg_` token.
3. Agent `POST /ingest/telemetry` with `Authorization: Bearer agt_02HW`, a
   `heartbeat` item → 200 `{v:1, accepted:true, commands:[]}`. Server's
   "agent silent" timer resets.
4. Agent `POST /ingest/logs` with one `log` item
   `record_id:"41235", dedup_scope:"System"` → 200 `{accepted:1, deduped:0}`.

### 18.2 Channel clear — epoch bump
- Channel `System` is cleared (`wevtutil cl System`) after RecordID reached
  `53000`. Agent detects (bookmark invalid or RecordID regression), bumps epoch
  to `1`, resubscribes from "now".
- Next event RecordID `1` → envelope `record_id:"e1:1", dedup_scope:"System"`.
- Server stores it as a new row (distinct from the old `"1"` which no longer
  exists after the clear anyway, and distinct from any pre-clear RecordID).
- Agent emits synthetic `channel_reset` event under `record_id:"e1:0"` /
  `dedup_scope:"System"` (or omits; implementation choice — it MUST be
  idempotently-keyed), sets `degraded:"channel_reset"` in heartbeat.

### 18.3 Backend-down → spool → drain
1. Agent has spool files `…-0007a.json` … `…-0009.json`; backend returns
   `503`. Sender keeps spool files, backs off 1→2→4→…s (+jitter), CPU ~0.
2. Backend recovers. Sender POSTs `0007a` → 200 `{accepted:50}`; deletes file.
   POSTs `0007b` → 200; deletes. Etc.
3. A retried `0007a` after the server had already (partially) committed?
   Impossible here (we got 5xx), but if it had: `deduped` reflects the collapse.

### 18.4 Rate-limited rogue agent
- A compromised agent floods `/ingest/logs`. Server: per-source cap hits →
  `429` + `Retry-After: 30`. Other sources unaffected (per-source budget).

---

## 19. Open / forward

1. **Registration token reuse vs one-shot** — decided single-use (§5.2).
   Reinstall reuses the `source_id` via hostname+kind match; admin reissues a
   fresh `reg_` token. (This resolves AGENT §20 #3.)
2. **Command channel** — reserved (§16); full spec deferred (DESIGN §12).
3. **Linux agent severity/facility** — TBD with the agent (§10).
4. **Syslog receiver transport** — separate spec (DESIGN §11 #3, Phase 3).
5. **`GET /health` strictness on bad tokens** — allowed lenient (§8.1); tighten
   to 401 if we want a hard token check at preflight without a real POST.
6. **Cumulative vs interval counters in heartbeat** — let the agent choose
   cumulative (server diffs by `source_id`+`time`); finalize in the agent.
7. **Idempotent registration/recovery flow** — response loss currently
   requires admin reissue (§5.4); a stable client installation ID + recovery
   endpoint would remove the manual step.
8. **Stable machine identity** — would allow true cross-host hostname
   collision handling; v1 accepts `(kind, hostname)` as authoritative (§5.2).

---

## Appendix A — Change log

| Date | Version | Notes |
|---|---|---|
| 2024-08-07 | v1 (draft) | Initial: registration, two ingest endpoints, health, idempotency+epoch, command reservation |
| 2026-08-09 | v1 (rev) | Clarity-only revision (no wire changes): `commands: []` in §5.3/§8.2 examples; version-mismatch response + validation precedence (§3); `(kind, hostname)` identity rule and `boot_id` semantics (§5); response-loss recovery procedure (§5.4); telemetry latest-wins ordering rule + facts empty/stale semantics (§7.4); size-limit semantics for gzip/chunked/encoding (§12); stable codes for 408/502/503/504 and proxy-error note (§13.3); machine-readable schema reference (§13.5); per-stream retry limits + split special case (§14); new open items (§19) |
| 2026-08-09 | v1 (rev) | Documentation-only: added §6.6 — server-side `logon_stats` aggregate: curated Security items (4624 LogonType 2/10, 4625, 4740, `TargetUserName`) are load-bearing; dedup-safe counting |

---

*This is the wire contract. The agent (AGENT.md) and server implement against
it; changes here are binding and versioned per §3.*