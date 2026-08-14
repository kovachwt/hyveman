# Security Review — 2026-08-14

Independent fresh-eyes review of auth/security across all three components
(hyveman-api, hyveman-agent, hyveman-web) plus the wire protocol and design.
Scope: everything except `docs/SECURITY-AUDIT.md`, which was deliberately not
read before forming findings; the comparison with that earlier audit appears
at the end of this document.

Severity scale used here: **M** = medium (real exploit path or data-integrity
impact, warrants fixing soon), **L** = low (hardening / defense-in-depth /
edge case). No high/critical findings.

---

## Medium severity

### M1. Redfish member-link following can leak iDRAC credentials (SSRF + credential exfiltration)

**Status: fixed 2026-08-14.** `DellRedfishProvider.GetJsonAsync` now resolves every
link against the host's base URI and refuses anything off-origin before a
request is built — scheme, host **and port** compared explicitly, and any
userinfo on the target forbidden (the initial `Uri.Authority` comparison was
insufficient: .NET's `Authority` excludes userinfo, so
`https://root:calvin@idrac.example/...` smuggled through — caught by the
regression test). Evil members are skipped with a warning; same-origin
absolute links are followed. API.md §9.1/§12 updated; regression test
`Poll_OffOriginLinks_RefusedWithoutRequest` (poisoned fixtures, asserts zero
requests leave the origin). Covers SECURITY-AUDIT S3.

`hyveman-api/src/Hyveman.Infrastructure.Redfish/DellRedfishProvider.cs`

`GetJsonAsync(client, baseUri, link.GetString()!, auth, ...)` builds
`new Uri(baseUri, path)` where `path` is an `@odata.id` value taken verbatim
from the iDRAC's JSON response (`CollectLinkedMembersAsync`,
`CollectStorageAsync`). If a compromised or MITM'd iDRAC returns
`"@odata.id": "https://attacker.example/x"`, `new Uri(base, absoluteUri)`
resolves to the absolute attacker URL — and because the
`Authorization: Basic <user:pass>` header is set manually on the request,
HttpClient sends the iDRAC credentials to that host (explicit headers are not
subject to same-origin restrictions). It is also a general SSRF primitive
from the backend's network position. `AllowAutoRedirect = false` is already
set — this path bypasses that intent entirely.

**Fix:** before fetching, require `@odata.id` to be a relative path
(`StartsWith("/")`, no scheme/authority) and rooted under the host's base
URI; otherwise skip the member.

### M2. Unauthenticated requests are parsed before auth and share the global rate budget → fleet-wide ingest starvation & log loss

**Status: fixed 2026-08-14.** Credentials and budgets now run before the body
is read/parsed; unauthenticated traffic no longer consumes the global budget
(`AgentProtocolMiddleware` restructure + new
`RateLimits:AgentNetworkPerMinute`, default 300/min, applied to every agent
endpoint before any database or body work; `/health` consumes no global
budget). PROTOCOL §15 and API.md §5.1/§6.6 updated; regression tests in
`ApiContractTests` (per-network flood) and `EdgeCaseContractTests`
(credential-before-body precedence).

`hyveman-api/src/Hyveman.Api/AgentProtocolMiddleware.cs`

Order of operations: global rate check → read up to 4 MiB + JSON parse +
`v` check → `AuthenticateIngestAsync`. `AcquireGlobal` is a single
process-wide bucket (1200/min) shared by all clients. An unauthenticated
attacker spamming garbage bodies can (a) burn CPU on 4 MiB JSON parses and
(b) exhaust the global bucket, so every legitimate agent gets 429 → agents
spool → bounded spool drops oldest events → permanent log loss. Per-source
limits only apply after auth; `/ingest/*` has no per-network budget (only
`/register` does).

**Fix:** check the bearer token before reading/parsing the body; add a
per-network limiter for unauthenticated agent-endpoint requests (the
machinery already exists in `RateLimiterRegistry`).

### M3. Invitation consumption is not atomic — one invite can create multiple accounts

`hyveman-api/src/Hyveman.Infrastructure.Security/WebAuthnService.cs`
(`CompleteRegistrationAsync`)

Invite validity check → user creation → passkey insert →
`MarkConsumedAsync` are separate DB operations with no transaction, and
`MarkConsumedAsync`'s return value (`... AND consumed_at IS NULL`) is
ignored. Two concurrent verifies with the same `inv_` token can both pass
`IsValidInvite` and both create accounts + sessions. Contrast
`RegistrationUnit`, which performs the `reg_` check-and-consume correctly
inside `BEGIN IMMEDIATE`.

**Fix:** at minimum, check the `MarkConsumedAsync` return value and roll
back; better, reuse the immediate-transaction unit pattern.

---

## Low severity

### L1. WebAuthn production config validation is dead code

`Program.cs` defaults `WebAuthnRpId`/`WebAuthnExpectedOrigin` to
`localhost`/`http://localhost:5080` at bind time, so the later
`if (string.IsNullOrEmpty(...)) throw` startup check can never fire. A prod
deployment that forgets to configure them silently runs RP-ID `localhost`
(login fails closed, but confusingly), and `CsrfMiddleware.IsAllowedOrigin`'s
"any localhost origin" dev branch stays active. Default only in
`Development`.

### L2. Audit-log JSON injection via hostname

`RegistrationService.RegisterAsync` builds `detail_json` by string
interpolation: `$"{{\"kind\":\"{kind}\",\"name\":\"{hostname}\"}}"`.
`hostname` is only length/whitespace-checked (1–255), so quotes/braces
produce malformed or injected audit JSON. `agentVersion`/`osBuild` are
escaped via the `Json()` helper right next to it — `kind`/`name` should be
too (or serialize a real object). Impact: audit integrity only; React
escapes on render.

### L3. Setup-mode race

Both the begin-time and verify-time "users table empty" checks in
`WebAuthnService` are non-atomic; two concurrent trusted-network setups can
create two users. Gated to the trusted network, so low.

### L4. TOFU window on first iDRAC poll

With `trust-on-first-use` and no pin yet, any certificate is accepted and
pinned (`DellRedfishProvider` TLS callback). A MITM present at first poll
becomes permanently trusted. Inherent to TOFU and documented, but it is also
the delivery path for M1 — worth requiring operator confirmation of the
first pin, or at least a loud notification.

### L5. Unbounded rate-limiter registry

`RateLimiterRegistry._limiters` never evicts; every distinct
`RemoteIpAddress` string permanently allocates a `FixedWindowLimiter` (auth
+ registration buckets). An IPv6 attacker rotating source addresses grows
the dictionary without bound; IPv6 canonicalization also duplicates keys.
Add periodic eviction of idle buckets.

### L6. Custom-CA agent trust skips hostname validation

`hyveman-agent/src/Hyveman.Agent/Net/BackendClient.cs`: with
`backend.ca_path` set, the callback does `chain.Build(cert)` against
`CustomRootTrust` only; the TLS hostname check SslStream normally performs
is bypassed, so any cert from the private CA is accepted for any backend
name. Also compare the cert's DNS name, or use default OS validation plus
the CA in the machine store.

### L7. Regex DoS surface in alert rules

`AlertEvaluatorService` compiles admin-supplied `messagePattern` with no
match timeout and runs it against agent-supplied event messages. A
catastrophically backtracking pattern (accidental or hostile) hangs ingest
evaluation — the try/catch there contains throws, not hangs. Use
`new Regex(pat, opts, TimeSpan.FromSeconds(1))` or
`RegexOptions.NonBacktracking`.

### L8. Missing API security headers

No HSTS / `X-Content-Type-Options: nosniff` / `X-Frame-Options` on API
responses. The SPA's CSP (`index.html`) is decent but hardcodes
`connect-src http://127.0.0.1:5080 http://localhost:5080` — shipped to
production builds too, where same-origin should suffice. Best fixed at
nginx + a prod-only CSP.

---

## Verified correct (no action)

- **Tokens**: all token kinds are 192–256-bit CSPRNG, stored SHA-256-hashed
  (unsalted is fine at that entropy), raw values returned exactly once,
  revocation works, scopes enforced (`wrong_scope` vs `401` distinction).
- **Sessions**: HttpOnly + Secure + SameSite=Strict, server-side revocable,
  user-bound, disabled/deleted users re-checked per request, non-compounding
  sliding expiry, new session id at login (no fixation).
- **CSRF**: double-submit header/cookie with `FixedTimeEquals` +
  Origin/Referer allowlist on unsafe methods; applies to
  login/register/logout/inspect too.
- **WebAuthn**: single-use challenges (deleted on take), 5-min expiry,
  ceremony-mode re-validation at verify (trusted-network re-check,
  invite-id binding, session user authoritative — the body is never
  trusted), exclude-credentials prevents double enrollment, constant-time
  user-handle comparison, sign-count verification via Fido2NetLib, RP origin
  pinned via config.
- **Authorization**: fallback policy requires auth everywhere; OpenAPI
  protected in prod; `/health/*` anonymous by design; passkey/user lockout
  guards (self/last-user/last-passkey) present in `UsersService`.
- **Injection**: SQL fully parameterized via Dapper; FTS5 query
  quoted/escaped; cursors strictly parsed; no dynamic SQL from user
  strings. No `dangerouslySetInnerHTML` anywhere in the SPA.
- **Ingest hardening**: gzip decompression capped (4 MiB streamed check),
  1000 items/batch, per-item size caps with per-item rejection, global
  UNIQUE idempotency key.
- **Logon curation**: server-side re-filters 4624 to LogonType 2/10
  regardless of agent claims; DWM-/UMFD- noise excluded.
- **Forwarded headers**: loopback-only proxy trust by default;
  `RemoteIpAddress` used post-rewrite for the trusted-network gate and rate
  buckets (correct middleware order).
- **Vault**: AES-GCM with fresh nonce per value, random key file, `0600` on
  Linux (Windows ACL left to the operator — worth a doc note).
- **Agent**: outbound-only, no listener, token discarded after exchange,
  installer ACLs data dir to SYSTEM+Admins, spool bounded.

---

## Comparison with SECURITY-AUDIT.md

(Written after the findings above were finalized.)

### Both reviews independently agree

| This review | Earlier audit | Notes |
|---|---|---|
| M1 | S3 (P2) | Near-identical finding and fix (reject non-relative `@odata.id`); found independently by both |
| L6 | D19 | Agent custom-CA mode skips hostname validation |
| L5 | D24.7 | Rate-limiter registry never evicts keys |
| L4 | (inside S3's scenario) | TOFU first-poll window; the audit mentions it as S3's delivery path, this review breaks it out standalone |
| L1 | S4 (second half) | Both flag the localhost-shaped defaults; S4 additionally details the CSRF substring flaw itself |

The two "verified correct" lists also agree (SQL parameterization, hashed
tokens, write-only secrets, authorization coverage, frontend XSS, no
committed secrets) — decent cross-validation of the clean surface.

### Earlier findings still open (confirmed present in current code)

- **S1 + S2 (P1)** — first-run setup gate spoofable via `X-Forwarded-For`
  from a loopback proxy that forwards the client-supplied header, because
  `TrustedNetwork` trusts loopback unconditionally and `ForwardedHeadersOptions`
  is constructed inline with no way to bind `KnownProxies`/`ForwardLimit`.
  Both code patterns are unchanged as of this review.
- **S4** — CSRF origin allowlist's localhost substring branch still active
  whenever any configured origin contains "localhost".
- **S5** — session/CSRF cookie `Secure` flag still conditional on
  `Request.IsHttps` (i.e. on `X-Forwarded-Proto` being honored).
- **S6** — SMTP notifier still vault-stores credentials it never sends and
  defaults `useTls` to false.
- **S7** — `TrustedNetwork.Parse` still accepts a negative CIDR prefix
  (`int.TryParse("-1")`), which fails open to match-everything.
- **S9** — anonymous `setupRequired` still advertises the window.
- **D17, D19, D20, D24.7, D24.11** — vault key has no ACL on Windows; agent
  pinned-CA hostname skip; login UV `Discouraged`; limiter eviction; webhook
  plain-HTTP allowed.

### Earlier findings fixed since 2026-08-10 (observed in current code)

- **S8** — ceremony gate re-validation at verify is implemented (the code
  comment cites it; invite re-validation included).
- **D24.3** — `auth reset` now deletes `web_sessions` (and invitations,
  challenges) alongside users.

### New findings from this review (absent from the earlier audit)

- **M2** — pre-auth body parse + shared global rate budget → unauthenticated
  ingest starvation and eventual permanent log loss via bounded spool
  drop-oldest. The earlier audit's "what the review did not find" section
  actually praised the ingest hardening without examining the ordering.
- **M3** — invite consumption is not atomic; `MarkConsumedAsync`'s guarded
  return value is ignored. Notably, the earlier audit's multi-user addendum
  describes invite re-validation happening "before the commit transaction
  that creates the user + passkey" — **no such transaction exists in the
  current code** (separate statements with best-effort compensation only),
  so that note overstated the control even when written.
- **L2** — audit-log detail_json built by string interpolation of
  attacker-influenced hostname (registration path).
- **L3** — setup-mode TOCTOU race on the users-empty check (distinct from
  S8, which was about re-checking at verify, not atomicity).
- **L7** — unbounded backtracking (ReDoS) surface in admin-supplied alert
  `messagePattern` evaluated against agent-supplied messages.
- **L8** — missing API security headers (HSTS, nosniff, frame-options) and
  a dev `connect-src` baked into the production CSP.

### What the earlier audit caught that this review missed

Recorded plainly, since that is the point of the comparison:

- **S1 + S2 (P1)** — the composite XFF attack on the first-run setup gate.
  This review verified the individual controls (loopback-only proxy trust;
  loopback always trusted for setup) but failed to compose them into the
  spoofing attack, and did not notice `KnownProxies` is documented yet
  unbindable. The most significant gap in this review.
- **S5** — this review listed the session cookie as `Secure` without
  noticing the flag is conditional on the request scheme and therefore on
  `X-Forwarded-Proto` being applied.
- **S7** — the negative-prefix CIDR fail-open was missed despite reading
  `TrustedNetwork.cs` (integer edge case not chased).
- **S6 / D24.11 / D20** — SMTP credentials stored-but-unused + TLS off,
  webhook plain-HTTP, login UV discouraged: code was read, not flagged.

Common root cause for the misses: this review evaluated proxy-dependent
controls against the *intended* deployment (nginx on loopback doing the
right thing), while the earlier audit varied the proxy configuration
adversarially. The S1/S2/S5 family all live in that gap. Lesson for the
next pass: treat the reverse proxy as an attacker-controlled trust
boundary unless its exact config is pinned in code.

### Net assessment

Merging both lists, the priority order for open work:

1. **S1 + S2** (P1, earlier audit) — setup-gate XFF spoofing + proxy trust
   binding; still the highest-severity open items.
2. **M2** (new, **fixed 2026-08-14**) — pre-auth parse + global-budget
   starvation → log loss.
3. **M1 / S3** — Redfish credential exfiltration via absolute links
   (**fixed 2026-08-14**, see M1 above).
4. **M3** (new) — invite atomicity; also correct the audit-doc description.
5. **S5, S7, L1/S4** — fail-closed config/cookie/CIDR fixes (small).
6. **L2, L5/D24.7, L6/D19, L7** — integrity and DoS hardening.
7. **S6, L8, S9, D17, D20, D24.11** — remaining hardening backlog.
