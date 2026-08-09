# Hyveman Protocol v1 — Review Notes

**Status:** Review notes only; no protocol behavior has been changed here.

> **Resolution status (2026-08-09):** all items below were resolved in
> `docs/PROTOCOL.md` rev 1 (clarity-only revision — no wire-semantic changes,
> protocol version stays `1`) plus the agent implementation. See the
> resolution table at the end of this file. The original notes are preserved
> as the record of the review.

`PROTOCOL.md` is treated as the implemented v1 agent contract. The notes below
identify ambiguities and possible improvements for a future revision or an
implementation clarification. Because the agent is already implemented, any
wire-semantic change should be assessed for backward compatibility before being
adopted.

## Items worth resolving before extending the protocol

### 1. Reserved `commands` field is missing from two examples

§16 says every successful and error response must include:

```json
"commands": []
```

The registration response in §5.3 and health response in §8.2 omit the field.
Keep the invariant or explicitly exempt those endpoints. Keeping the invariant
and adding the field to both examples is the simpler option.

### 2. Unsupported-version response semantics are ambiguous

§3 requires the request and response protocol versions to match, but a client
using an unsupported version cannot receive a response using that same version.
Define an exception for `missing_version`, `unsupported_version`, and header/body
version mismatches. A practical rule is that these responses use the server's
current protocol version in the response header/body and identify supported
versions in `error.supported`.

Also define validation precedence when the protocol header and JSON body contain
different versions.

### 3. Registration response loss can orphan an agent

The server consumes the one-time `reg_` token before returning the long-lived
`agt_` token. If the response is lost after the transaction commits, the agent
cannot recover the token and a retry receives `410 token_consumed`.

Possible future solutions include an idempotent registration/recovery flow, a
stable client installation ID, encrypted storage of a pending registration
response, or an explicit admin reissue procedure. The behavior should be
intentional and documented before unattended installation is expanded.

### 4. Hostname reuse and collision handling need one identity rule

Registration reuses a source by `(kind, hostname)`, but the document also says
distinct physical hosts sharing a hostname can be detected and disambiguated.
That is not possible if `boot_id` is optional and is not part of source
resolution.

Choose and document one approach:

- store and compare a stable installation/machine identity;
- treat `(kind, hostname)` as authoritative and remove the collision claim; or
- require operator confirmation when the identity is ambiguous.

If `boot_id` is retained, define whether it is stable across reboots and
reinstalls. The name commonly implies a value that changes on every boot.

### 5. `413 payload_too_large` conflicts with retry behavior

§12 says the agent should split a batch after `413 payload_too_large`. §14 says
4xx responses are quarantined and explicitly says the agent does not re-split a
batch.

Choose one behavior. If splitting is supported, make `413` a documented special
case with a bounded split algorithm. If it is not supported, remove the
instruction to split and require the agent to guarantee the limit before
sending.

### 6. Telemetry needs an ordering rule

The telemetry endpoint is described as latest-wins, but “latest” is not defined
as request arrival time, `sent_at`, `collected_at`, or a sequence number. A
reordered older request could overwrite newer current state.

A robust rule would be:

- compare heartbeat payloads using a boot/session identity and `sent_at`;
- compare facts using `collected_at`;
- store server `received_at` independently; and
- use server receive time for silence detection.

The response behavior for a valid but older payload should also be defined.

### 7. Add a formal v1 JSON Schema

The examples and prose do not fully define:

- required versus optional log fields;
- whether `severity` is omitted for Windows level `0`;
- the type of `raw`;
- required Windows fields such as `fields.channel`;
- nested object/array rules for `fields`;
- maximum total serialized `fields` size and nesting depth;
- whether size limits are UTF-8 bytes or characters; and
- valid ranges and units for telemetry values.

A versioned schema such as `schemas/protocol-v1.json` would make agent/server
validation and contract testing much less ambiguous.

### 8. Registration-token persistence is underspecified

The protocol requires registration tokens to be bound to a source kind,
single-use, expirable, revocable, and marked consumed. The core `tokens` table
in `DESIGN.md` does not explicitly contain all registration-token metadata.

Use a dedicated `registration_tokens` table or document the additional token
columns and lifecycle states.

## Additional clarifications

### 9. Standardize error codes for gateway/timeouts

The error table has no stable codes for `408`, `502`, and `504`, and permits a
missing code for `503`. Define stable machine codes for all protocol errors and
define how reverse-proxy-generated errors preserve the protocol JSON envelope.

### 10. Separate compressed and uncompressed size limits

The request-size table refers to `Content-Length`, while gzip is optional. The
protocol should define both:

- maximum compressed request size; and
- maximum decompressed JSON size.

It should also define behavior for chunked requests and unsupported
`Content-Encoding` values.

### 11. Clarify retry limits

§14 describes telemetry as having three attempts but later describes retries as
unbounded. State explicitly that log retries may continue while spooled, while
telemetry has a maximum number of attempts per interval. Consider a maximum
accepted `Retry-After` delay as well.

### 12. Define timestamp and clock-skew handling

Log event times are required to be UTC, but the protocol does not define an
allowed future/past skew or whether offsets other than `Z` are accepted. Since
retention, searches, and alert windows depend on time, define normalization and
whether the server stores a separate receive time for events.

### 13. Clarify facts snapshot semantics

For `facts` items, define whether an empty `vms` array means “the host has no
VMs” or “the scan failed,” how `stale` affects replacement, and how multiple
facts items in one request are ordered. This is especially important when a WMI
scan times out.

### 14. Revisit the future command-token relationship

The protocol reserves commands in responses to agent requests, while also
reserving a future `command-pickup` token scope. A future command specification
should define whether command-bearing responses require that scope, whether an
agent uses two tokens, and how scope failures are represented.

## Existing strengths

The following decisions are clear and worth preserving:

- HTTPS-only transport with no inbound agent listener;
- token-derived source identity rather than trusting the request body;
- separate registration, durable logs, telemetry, and health semantics;
- idempotency via `(source_id, dedup_scope, record_id)`;
- epoch-prefixed Windows record IDs after channel resets;
- per-item log rejection with whole-batch retry for transient failures;
- bounded request/item/raw/message limits;
- per-source and global rate limiting; and
- reserving the `commands` response slot before implementing commands.

## Resolution status (2026-08-09)

Each item above, and where it was addressed:

| # | Item | Resolution |
|---|---|---|
| 1 | `commands` missing from §5.3/§8.2 examples | PROTOCOL.md §5.3/§8.2: `"commands": []` added to both examples; invariant kept (§16). |
| 2 | Unsupported-version response semantics | PROTOCOL.md §3: version-mismatch exception (server answers with its own version + `error.supported`) and validation precedence (header absent → `missing_version`; unsupported → `unsupported_version`; body ≠ header → `invalid_request`). |
| 3 | Registration response loss | PROTOCOL.md §5.4: documented response-loss recovery (reissue `reg_` token; §5.2 reuse path keeps `source_id`). Agent: `Program.cs` now catches network failures mid-exchange with a clean diagnostic (previously an unhandled exception); fail-closed behavior unchanged. Idempotent recovery flow tracked in §19 item 7. |
| 4 | Hostname reuse / identity rule | PROTOCOL.md §5.2: `(kind, hostname)` declared authoritative for v1; collision-disambiguation claim removed; `boot_id` redefined as a per-boot identifier, explicitly not part of source resolution. `409 name_collision` retained as reserved. §19 item 8 tracks a future stable machine identity. |
| 5 | `413` vs. no-resplit contradiction | PROTOCOL.md §12/§14: `413 payload_too_large` and `400 too_many_items` are now a documented special case overriding the no-resplit rule, with the bounded recursive-halving algorithm (matches the existing agent behavior in `EnvelopeBuilder.SplitInHalf`). §6.5 updated to name the exception. |
| 6 | Telemetry ordering | PROTOCOL.md §7.4 (new): latest-wins defined — heartbeat compares `boot_time` (new session) then `sent_at`; facts compare `collected_at`; `received_at` stored independently; silence detection on server receive time; older payloads still get `200`. |
| 7 | Formal JSON Schema | `docs/schemas/protocol-v1.json` (draft-07) added; referenced from PROTOCOL.md §13.5; validated against all §5–§8 examples with Ajv. |
| 8 | Registration-token persistence | Server-side design item; covered by PROTOCOL.md §4.1/§5 (token hash storage, `reg_` single-use/expirable/revocable/consumed). No further action. |
| 9 | Gateway/timeout error codes | PROTOCOL.md §13.3: stable codes assigned — `408 request_timeout`, `502 bad_gateway`, `503 unavailable`, `504 gateway_timeout`, plus `415 unsupported_media_type`; proxy-generated errors may lack the envelope and agents classify by status (§14 — the agent already does). |
| 10 | Compressed vs. uncompressed limits | PROTOCOL.md §12: limits defined on the decompressed JSON; gzip only reduces wire bytes; chunked requests bounded by the same cap; unsupported `Content-Encoding` → `400 invalid_request`/`415`. |
| 11 | Retry limits | PROTOCOL.md §14: per-stream limits stated — logs unbounded while spooled (60 s per-attempt cap), telemetry ≤ 3 attempts per interval with immediate discard of non-retriable outcomes; `Retry-After` honored but capped at 3600 s per wait. Agent: `LogSender` now caps `Retry-After`; `TelemetrySender` short-circuits non-retriable outcomes and surfaces `auth_rejected` on credential-class 4xx. |
| 12 | Timestamp / clock skew | PROTOCOL.md §10/§12 and the schema: UTC ISO-8601 with trailing `Z`, ms precision; server-side skew policy left to the server implementation (no wire change needed). |
| 13 | Facts snapshot semantics | PROTOCOL.md §7.1/§7.4: empty `vms` + `stale:false` = host has no VMs; failed scans re-emit prior facts with `stale:true`; multiple facts items applied in array order under the `collected_at` rule. Matches the existing agent (`WmiFactCollector`). |
| 14 | Future command-token relationship | Deferred to the future command-channel spec (PROTOCOL.md §16); §19 notes the relationship question is open. No v1 action. |

Agent-side code changes made alongside this resolution (all optional, none
wire-semantic): `Program.cs` registration try/catch; `TelemetrySender`
short-circuit + `auth_rejected`; `BackendClient` `X-Hyveman-Source` only when
non-empty + ingest-scope check on register response; `LogSender` `Retry-After`
cap (3600 s).
