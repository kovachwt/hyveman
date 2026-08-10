# Hyveman — Security Audit

**Status:** open findings from a security-focused review (2026-08-10)
**Scope:** `hyveman-api`, `hyveman-agent`, `hyveman-web` — authentication and
session handling, the agent ingest path, the credential vault, outbound
integrations (Redfish, notifiers), and the browser-facing surface.

This document tracks *security weaknesses*, which is a different axis from
`DEFECTS.md` (deviations from the design contracts). The two overlap: several
already-tracked defects have a security consequence, and where that is the case
this document points at the existing `D`-id rather than restating it. Findings
that are new here carry an `S`-id.

All findings are **by inspection** — none were reproduced against a running
instance. They should be confirmed before a fix is accepted, and the
proxy-dependent ones (S1) should be confirmed against the *actual* reverse-proxy
configuration in use, not the one in the runbook.

Severity: **P1** = remote compromise, credential disclosure, or auth bypass;
**P2** = meaningful weakening of a control, or exposure requiring a second
condition; **P3** = hardening, defense-in-depth, or robustness.

| ID | Sev | Component | Summary |
|---|---|---|---|
| [S1](#s1) | P1 | api | First-run setup gate is reachable via a spoofed `X-Forwarded-For` |
| [S2](#s2) | P1 | api | `KnownProxies` is documented but never bound — forwarded-header trust is unconfigurable |
| [S3](#s3) | P2 | api | Redfish poller follows absolute `@odata.id` links with iDRAC credentials attached |
| [S4](#s4) | P2 | api | CSRF origin allowlist accepts any localhost origin in localhost-shaped configs |
| [S5](#s5) | P2 | api | Session cookie `Secure` flag depends on `Request.IsHttps` |
| [S6](#s6) | P3 | api | SMTP notifier stores credentials it never uses; TLS defaults off |
| [S7](#s7) | P3 | api | Negative CIDR prefix in `TrustedSetupNetworks` matches every address |
| [S8](#s8) | P3 | api | Registration-verify does not re-check the setup gate |
| [S9](#s9) | P3 | api | Unauthenticated `setupRequired` advertises the S1 window |

Already tracked in `DEFECTS.md`, listed here so the security picture is complete:
**D17** (vault key gets no ACL on Windows — P1 in security terms: any local user
can read `vault.key` + `hyveman.db` and decrypt every stored iDRAC, Telegram, and
SMTP secret), **D18** (loopback unconditionally trusted for first-run setup — see
S1, which sharpens it from a misconfiguration into an attack), **D19** (agent
pinned-CA mode skips hostname validation), **D20** (login ceremony sets user
verification to `Discouraged`), **D24.3** (`auth reset` leaves web sessions
live), **D24.7** (rate-limiter keys never evicted), **D24.8** (out-of-range CIDR
prefix throws at request time — see S7), **D24.11** (webhook targets may use
plain `http`).

---

<a id="s1"></a>
## S1 — First-run setup gate is reachable via a spoofed `X-Forwarded-For` (P1)

**Location:** `hyveman-api/src/Hyveman.Api/TrustedNetwork.cs:21`,
`hyveman-api/src/Hyveman.Api/Program.cs:225-230`
**Contract:** API.md §8.1, DESIGN §8 — first-run registration is permitted only
"from the configured localhost/trusted network".
**Relationship to D18:** D18 records the unconditional loopback branch and frames
the risk as "if the proxy is misconfigured, or a deployment fronts the API
without XFF". This finding is the active form: an attacker can *cause* the
loopback presentation rather than waiting for a deployment that lacks XFF.

```csharp
if (IPAddress.IsLoopback(ip)) return true; // localhost is always trusted
```

```csharp
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    // trust only loopback proxies by default; deployers with a remote
    // proxy must add their proxy addresses to KnownProxies
});
```

Three properties compose:

1. `UseForwardedHeaders` rewrites `Connection.RemoteIpAddress` from
   `X-Forwarded-For` when the immediate peer is a known proxy. ASP.NET's default
   known set is loopback — and the documented topology (INSTALL.md:85) puts the
   reverse proxy on `127.0.0.1`, so XFF *is* honored in the intended deployment.
2. `TrustedNetwork` then treats a loopback value as trusted unconditionally, so
   the operator cannot remove it. An install following INSTALL.md:164
   (`"TrustedSetupNetworks": ["10.0.0.0/8"]`) still trusts `127.0.0.1`.
3. INSTALL.md:101 instructs the operator only to *"preserve"* `X-Forwarded-For`.
   A proxy that forwards the client-supplied header through unchanged — e.g.
   `proxy_set_header X-Forwarded-For $http_x_forwarded_for` — satisfies that
   wording while letting the client choose the value.

**Failure scenario.** The `passkeys` table is empty: a fresh install before the
operator reaches INSTALL.md §6, a restore that cleared passkeys, or any time
after `hyveman-api auth reset`. A remote attacker sends
`X-Forwarded-For: 127.0.0.1` to `POST /api/v1/auth/passkeys/register/options`.
`WebAuthnService.cs:52` — the only gate — sees a trusted remote and issues
ceremony options; the attacker completes the ceremony with their own
authenticator and holds the sole admin credential for an internet-exposed
console. `AuthController.cs:60,71` are both `[AllowAnonymous]`, as they must be
for the wizard to work at all.

The window is narrow but it is exactly the window in which nobody is watching:
a new install has no alerting configured yet, and `auth reset` is the
lockout-recovery path, run by an operator who is already distracted.

**Fix.** Both halves, independently:

- Drop the unconditional loopback branch and rely on the configured list (the
  default `["127.0.0.1/32","::1/128"]` at `Options.cs:41` already covers local
  operation). This is D18's fix and is sufficient only if the operator narrows
  `TrustedSetupNetworks` — which most will not.
- Bind forwarded-header trust from configuration (S2) so the deployer can pin
  the proxy, and set `ForwardLimit` to the real hop count so only the value the
  proxy itself appended is honored.
- Consider requiring that first-run setup arrive on a listener the operator
  designates, rather than inferring trust from a header at all.

---

<a id="s2"></a>
## S2 — `KnownProxies` is documented but never bound (P1)

**Location:** `hyveman-api/src/Hyveman.Api/Program.cs:225-230`,
`hyveman-api/src/Hyveman.Api/Options.cs`
**Contract:** INSTALL.md:101-102 — "Preserve `X-Forwarded-Proto`/`X-Forwarded-For`;
the API trusts loopback proxies by default (add remote proxies to `KnownProxies`
— see API.md)."

`ForwardedHeadersOptions` is constructed inline with only `ForwardedHeaders` set.
`KnownProxies`, `KnownNetworks`, and `ForwardLimit` are left at their defaults
and are not readable from config: `HyvemanOptions` has no corresponding property,
and API.md — which INSTALL.md defers to — does not mention `KnownProxies`
anywhere. The runbook instructs the operator to turn a knob that does not exist.

**Failure scenario.** Two directions, both bad:

- A deployer with a *remote* reverse proxy follows INSTALL.md, finds nothing to
  configure in API.md, and ships. XFF from that proxy is silently ignored, so
  every request presents with the proxy's IP: rate limiting (`RateLimiter.cs:62`)
  degenerates to one shared bucket for the whole internet, audit and log
  attribution is wrong, and the S1 gate evaluates against the proxy address.
- A deployer with a *loopback* proxy has no way to constrain the hop count, so
  `ForwardLimit`'s default governs which XFF entry wins — the precondition for
  S1.

**Fix.** Add `KnownProxies` / `KnownNetworks` / `ForwardLimit` to
`HyvemanOptions`, bind them into `ForwardedHeadersOptions`, and document the keys
in API.md §11 alongside the other configuration surface. Fail startup loudly if
`ApiListenUrls` is non-loopback while no proxy trust is configured.

---

<a id="s3"></a>
## S3 — Redfish poller follows absolute `@odata.id` links with credentials attached (P2)

**Location:** `hyveman-api/src/Hyveman.Infrastructure.Redfish/DellRedfishProvider.cs:269`
(reached from `:205`, `:231`, `:246`)
**Contract:** API.md §12 — the Redfish client "must not follow arbitrary
redirects"; DESIGN §7 — "Secrets are never sent to agents/UI".

```csharp
using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
req.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
```

`path` is taken verbatim from the polled device's own JSON — the `@odata.id` of
each collection member, storage controller, and drive. `new Uri(base, relative)`
resolves a *relative* reference against the base, but an absolute URI in the
second argument replaces the base entirely: `new Uri("https://idrac.lan/",
"https://evil.example/x")` yields `https://evil.example/x`. The iDRAC Basic
credentials are then attached to that request.

This is the same threat the code already defends against one layer up:
`AllowAutoRedirect = false` is set twice (`Program.cs:159`,
`DellRedfishProvider.cs:183`) with a comment citing API.md §12. Link-following
reintroduces the hole the redirect ban closed.

**Failure scenario.** An attacker who controls or can impersonate an iDRAC — a
compromised BMC, or a `trust-on-first-use` install where the first poll was
intercepted (`:36-51` pins whatever the first connection presents) — returns a
`Storage` collection whose members are absolute URLs pointing at attacker
infrastructure. The poller walks them and delivers the iDRAC username and
password, base64-encoded, to the attacker. Those credentials typically grant
full out-of-band control of the host: virtual media, power, console.

Note that `HostsService.cs:94` correctly validates the operator-supplied
`idracUrl` (https only, no userinfo, no fragment). Only the device-supplied links
are unvalidated.

**Fix.** In `GetJsonAsync`, resolve the URI and then verify it before sending:
reject anything whose scheme is not `https` or whose authority differs from
`baseUri`'s. Rejecting non-relative `@odata.id` values outright at
`:204`/`:230`/`:245` is equivalent and cheaper to reason about.

---

<a id="s4"></a>
## S4 — CSRF origin allowlist accepts any localhost origin (P2)

**Location:** `hyveman-api/src/Hyveman.Api/Middleware.cs:147-162`
**Contract:** API.md §5.2/§8.2 — unsafe requests require "an allowed Origin (or
Referer), plus an anti-CSRF token supplied in a header and cookie pair".

```csharp
if (candidates.Count == 0 || candidates.Any(c => c.Contains("localhost", StringComparison.OrdinalIgnoreCase)))
{
    if (Uri.TryCreate(origin, UriKind.Absolute, out var o) &&
        (o.Host == "localhost" || o.Host == "127.0.0.1" || o.Host == "::1"))
        return true;
}
```

The intent (per the comment) is a dev convenience for the setup wizard running on
an arbitrary Vite port. The effect is broader: if *any* configured origin
contains the substring `localhost`, the origin check is disabled for all
localhost origins on all ports.

`Program.cs:45-46` defaults `WebAuthnExpectedOrigin` to `http://localhost:5080`
when unset, and the startup validation at `Program.cs:207` only asserts the value
is non-empty. So an install that never sets `WebAuthnExpectedOrigin` — or sets
`PublicOrigin` but not the WebAuthn one — starts successfully with the origin
check permanently in dev mode.

The double-submit CSRF token (`Middleware.cs:132-140`) still applies and is the
primary control, and the `hyveman_csrf` cookie is `SameSite=Strict`, so this is
not by itself an exploitable CSRF. What is lost is the second layer, silently,
in a configuration that looks valid.

**Fix.** Gate the localhost allowance on an explicit development flag
(`AllowInsecureHttp` already exists at `Options.cs:36` and carries exactly this
"dev/test only" meaning) rather than on substring-matching the production
origin config. Separately, make startup validation reject the built-in
`localhost` defaults when `ApiListenUrls` is not loopback.

---

<a id="s5"></a>
## S5 — Session cookie `Secure` flag depends on `Request.IsHttps` (P2)

**Location:** `hyveman-api/src/Hyveman.Api/Middleware.cs:61-72`, called from
`AuthController.cs:136` and `Middleware.cs:45`
**Contract:** DESIGN §8 — "persistent auth cookie, `HttpOnly; Secure;
SameSite=Strict`". API.md §8.2 states the same.

```csharp
Secure = isHttps,
```

`isHttps` is `Request.IsHttps`, which in the documented topology is true only
because `UseForwardedHeaders` promotes `X-Forwarded-Proto`. The API itself
listens on plain HTTP (`Options.cs:11`, INSTALL.md:160). A proxy that forwards
`X-Forwarded-For` but not `X-Forwarded-Proto` — or a deployment affected by S2,
where the proxy is not trusted and neither header is applied — issues the session
cookie and the CSRF cookie without `Secure`, contradicting a contract that states
the flag unconditionally.

The same conditional applies to the `hyveman_csrf` cookie at
`Middleware.cs:108`.

**Fix.** Set `Secure = true` unconditionally in production, with the dev opt-out
keyed to `AllowInsecureHttp` rather than to the observed request scheme. That
matches DESIGN §8's unconditional wording and fails closed when a header is
missing.

---

<a id="s6"></a>
## S6 — SMTP notifier stores credentials it never uses; TLS defaults off (P3)

**Location:** `hyveman-api/src/Hyveman.Infrastructure.Notify/Notifiers.cs:145-188`,
`hyveman-api/src/Hyveman.Application/ChannelsService.cs:158-159`
**Contract:** DESIGN §4.4 — "Channel secrets ... are stored as ciphertext in the
credentials vault"; API.md §7.4.

`ChannelsService.MergeConfig` collects and vault-encrypts `username` and
`password` for SMTP channels. `SmtpNotifier.SendAsync` reads `host`, `from`,
`to`, `port`, and `useTls` — and never the credentials. No
`SmtpClient.Credentials` is set, so an authenticating relay simply rejects the
send.

Separately, `useTls` defaults to `false` (`:161`) on port 587, so a channel
configured without an explicit `useTls` sends alert content — which includes
hostnames, component names, and event text — in cleartext.

Low severity because it is admin-configured and currently only affects a
deferred, low-priority channel (DESIGN §4.4 lists SMTP as optional). Recorded
because storing a secret that is never read is a liability with no benefit: it
sits in the vault, appears in backups, and implies a protection the code does not
provide.

**Fix.** Either wire the credentials into `SmtpClient.Credentials` and default
`useTls` to `true`, or stop collecting username/password until the channel is
actually implemented.

---

<a id="s7"></a>
## S7 — Negative CIDR prefix matches every address (P3)

**Location:** `hyveman-api/src/Hyveman.Api/TrustedNetwork.cs:26-33,44-52`
**Relationship to D24.8:** D24.8 covers the out-of-range *positive* case
(`10.0.0.0/999` indexes past the address bytes and throws). This is the opposite
sign, which fails open rather than throwing.

```csharp
if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) ||
    !int.TryParse(parts[1], out var prefix))
    return null;
```

`int.TryParse` accepts `-1`. With a negative prefix, `prefixBytes` is `0` and
`prefixBits` is negative, so the byte loop never runs and the `prefixBits > 0`
branch is skipped — `Contains` returns `true` for every address. A config typo
such as `"10.0.0.0/-1"` silently turns the first-run setup gate into
allow-everything.

`Parse` also returns `null` for any unparseable entry and the caller filters it
out (`:19-21`), so a malformed entry like `"10.0.0.0"` (no prefix) is dropped
with no warning. That direction fails closed, but just as silently.

**Fix.** Range-check `0 <= prefix <= 32` for IPv4 and `<= 128` for IPv6 (this is
D24.8's fix; extending it to reject negatives closes both). Log and fail startup
on any entry that does not parse, rather than dropping it — a trust list that
silently discards entries is worse than one that refuses to start.

---

<a id="s8"></a>
## S8 — Registration-verify does not re-check the setup gate (P3)

**Location:** `hyveman-api/src/Hyveman.Infrastructure.Security/WebAuthnService.cs:84-143`,
`hyveman-api/src/Hyveman.Api/AuthController.cs:70-78`

`BeginRegistrationAsync` enforces both halves of the policy — passkeys empty, or
an authenticated session; and trusted network for the first-run case
(`:46-56`). `CompleteRegistrationAsync` enforces neither. It is reachable
anonymously (`AuthController.cs:71`) and relies entirely on the caller holding a
challenge that only a gated `begin` call could have produced.

Not currently exploitable: the challenge is 32 random bytes, the ceremony record
is single-use and expires in five minutes (`:32`, `CeremonyStore.TakeAsync`), and
`ChallengeHash` lookups are keyed on the operation. The gap is that the security
property now rests on ceremony-store behavior rather than on an explicit check,
so a future change to ceremony storage or lifetime silently widens it.

**Fix.** Re-evaluate the same predicate in `CompleteRegistrationAsync` before
`passkeys.AddAsync`. It is two store reads on a once-per-install path.

---

<a id="s9"></a>
## S9 — Unauthenticated `setupRequired` advertises the S1 window (P3)

**Location:** `hyveman-api/src/Hyveman.Api/AuthController.cs:25-37`

```csharp
[HttpGet("session")]
[AllowAnonymous]
```

`SessionResponse.SetupRequired` is computed from `passkeys.CountAsync() == 0` and
returned to any unauthenticated caller. This is by design — the SPA needs it to
route to the wizard — but it also lets an attacker poll for exactly the moment
S1 becomes exploitable, including detecting an `auth reset` within seconds.

**Fix.** Nothing clean while the wizard is a public route; the honest mitigation
is closing S1 so the signal stops being actionable. If S1 is fixed by binding
setup to a designated listener, this endpoint can report `setupRequired` only on
that listener.

---

## What the review did not find

Recorded so a future audit knows what was covered and does not re-derive it:

- **SQL injection.** Every query uses Dapper parameters, including the FTS5
  `MATCH` term, which is quoted and escaped (`EventStore.cs:92-98`). The one
  string-interpolated statement is `VACUUM INTO` (`BackupStore.cs:23`), whose
  path is server-constructed from the data directory and a timestamp.
- **Token and session storage.** Agent tokens, registration tokens, and web
  session ids are SHA-256 hashed at rest and never stored raw
  (`StoreHelpers.cs:13`, `IdentityStores.cs`, `ConfigStores.cs:97`). All ids and
  tokens come from `RandomNumberGenerator`.
- **Secret exposure to the browser.** iDRAC and notification-channel secrets are
  genuinely write-only: responses carry a redacted summary and a boolean
  (`ChannelsService.cs:178-184`, `HostsService.cs:65`), and blank secret fields on
  patch mean "leave unchanged" (`ChannelsService.cs:140-165`).
- **Authorization coverage.** A fallback policy requires an authenticated user
  for every endpoint (`Program.cs:168-173`); the only `[AllowAnonymous]` routes
  are the four auth ceremony endpoints, `/health/live`, `/health/ready`, and — in
  Development only — the OpenAPI document.
- **Ingest hardening.** Bearer-only with scope checks, per-source and global rate
  limits, a 4 MiB decompressed body cap with an explicit gzip-bomb bound
  (`AgentProtocolMiddleware.cs:429-471`), per-batch item caps, and structural
  schema validation.
- **Frontend XSS and token handling.** No `dangerouslySetInnerHTML`, no `eval`,
  no innerHTML assignment; `localStorage` holds only the theme preference
  (`providers.tsx:45`). The session cookie is `HttpOnly` and the client never
  reads it (`api/client.ts`).
- **Committed secrets.** None. `devdata/` — which does contain a dev agent token
  and a dev iDRAC password — is gitignored (`.gitignore:52`) and untracked.
