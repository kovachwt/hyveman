# Hyveman — Local 3-Project Test Stack (generic)

Run all three projects together on a dev machine — agent → API → web — with
real data flowing end to end. This is the portable companion to the
machine-specific quickstart at `devdata/DEV-STACK.md` (gitignored, only exists
on the primary dev box); the `devdata/` directory itself is **never committed**.

Contract docs: [`docs/API.md`](docs/API.md) · [`docs/AGENT.md`](docs/AGENT.md) ·
[`docs/FRONTEND.md`](docs/FRONTEND.md) · [`docs/PROTOCOL.md`](docs/PROTOCOL.md).
Production install: [`INSTALL.md`](INSTALL.md).

## Topology

```text
hyveman-agent (console, .NET 8)        hyveman-api (dev, .NET 10)        hyveman-web (Vite)
 Event Log + WMI facts ──HTTPS──►      https://127.0.0.1:8443             http://localhost:5173
 /register, /ingest/*, /health          data dir: <repo>/devdata/api       proxies /api ──► https://127.0.0.1:8443
                                        config: devdata/api/config.json    (HYVEMAN_API_PROXY)
```

## Prerequisites

| Component | Required |
|---|---|
| .NET SDK **10.0** | builds `hyveman-api` |
| .NET SDK **8.0** | builds `hyveman-agent` |
| Node.js **22 LTS** + npm | `hyveman-web` (Vite 7) |
| A browser with a passkey authenticator | Windows Hello / platform authenticator for admin setup |

## 1. Build

```bash
dotnet build hyveman-api/Hyveman.Api.sln
dotnet build hyveman-agent/Hyveman.Agent.sln
cd hyveman-web && npm ci
```

(Optional, CI-grade: `dotnet test` both solutions, `npm run lint && npm run typecheck && npm run test -- --run`.)

## 2. Dev TLS certificate (one time)

The wire protocol mandates HTTPS; the dev stack uses a **self-signed** cert for
`localhost` / `127.0.0.1`. Generate one with your own CN/SANs and keep it out of
git (e.g. in `devdata/tls/`). PowerShell (Windows):

```powershell
$pwd = ConvertTo-SecureString 'dev-only-password' -Force -AsPlainText
$cert = New-SelfSignedCertificate -DnsName 'localhost' -CertStoreLocation Cert:\CurrentUser\My `
    -TextExtension @('2.5.29.17={text}dns=localhost&ipaddress=127.0.0.1&dns=your-hostname')
Export-PfxCertificate -Cert $cert -FilePath devdata\tls\dev.pfx -Password $pwd
Export-Certificate -Cert $cert -FilePath devdata\tls\dev.cer
# agent needs PEM or DER for ca_path pinning:
certutil -encode devdata\tls\dev.cer devdata\tls\dev.pem   # (or openssl x509 -in dev.cer -out dev.pem)
```

The browser also needs to trust this CA to view the API directly (health URLs,
OpenAPI) — visit `https://127.0.0.1:8443/health/live` once and accept, or add the
`.cer` to the machine/current-user **Trusted Root** store. The web dev server
does not need this (see §5).

## 3. Start the API

Create a data dir (e.g. `devdata/api`) with this `config.json` (first start
creates the DB, vault key, and logs):

```jsonc
{
  "ApiListenUrls": "https://127.0.0.1:8443",
  "AllowInsecureHttp": false,
  "Kestrel": {
    "Certificates": {
      "Default": { "Path": "C:\\path\\to\\devdata\\tls\\dev.pfx", "Password": "dev-only-password" }
    }
  }
}
```

```bash
# Windows (Git Bash): use forward slashes; a plain PowerShell window also works
ASPNETCORE_ENVIRONMENT=Development dotnet run --project hyveman-api/src/Hyveman.Api --no-launch-profile -- \
  --data-dir C:/path/to/devdata/api \
  --WebAuthnExpectedOrigin http://localhost:5173
```

- `--WebAuthnExpectedOrigin` **must equal the browser origin** (`http://localhost:5173`
  for the Vite dev server). The baked-in default is `http://localhost:5080`,
  which silently breaks passkey ceremonies — this is the #1 setup mistake.
- `ASPNETCORE_ENVIRONMENT=Development` enables the OpenAPI document
  (`https://127.0.0.1:8443/openapi/v1.json`).
- RP ID defaults to `localhost` — fine for dev; that is also why the browser
  must use `http://localhost:5173`, never `127.0.0.1:5173`.
- Sessions are DB-backed and survive API restarts; the agent self-heals
  (backoff + disk spool).

## 4. Start the agent

Config (`devdata/agent/agent.json`) — full template with the documented limits
(see `docs/AGENT.md` §6 for every field):

```jsonc
{
  "backend": {
    "url": "https://127.0.0.1:8443",
    "token": "",                       // empty → registration flow (below)
    "registration": { "token": "reg_…", "kind": "windows-agent" },  // omit once registered
    "ca_path": "C:\\path\\to\\devdata\\tls\\dev.pem",
    "validate_cert": true              // never disable outside a lab
  },
  "spool": { "dir": "C:\\path\\to\\devdata\\agent\\spool", "max_bytes": 104857600, "min_free_bytes": 5368709120 },
  "limits": {
    "process_memory_bytes": 268435456, "cpu_rate_percent": 25, "in_memory_queue_events": 10000,
    "batch_max_events": 500, "batch_max_age_ms": 1000, "max_batch_bytes": 4194304,
    "max_raw_bytes": 8192, "send_concurrency": 2, "send_timeout_ms": 30000, "gzip": true
  },
  "wmi": { "scan_interval_s": 60, "query_timeout_s": 20, "max_queries_per_scan": 8 },
  "heartbeat": { "interval_s": 30 },
  "security_log": { "enabled": true, "include_ids": [4624, 4625, 4740], "logon_types_for4624": [2, 10] },
  "channels": [
    { "name": "System", "level": "Warning" },
    { "name": "Application", "level": "Warning" },
    { "name": "HyvemanAgent", "channel": "Application", "provider": "HyvemanAgent", "level": "Information", "include_ids": [1, 2, 3, 4, 5] }
  ],
  "logging": { "level": "Debug", "dir": "C:\\path\\to\\devdata\\agent\\logs", "rolling": "10MBx5" },
  "data_dir": "C:\\path\\to\\devdata\\agent"
}
```

**Get an ingest token.** The proper way: after passkey setup (§6), the Sources
page can issue a single-use `reg_` token. Dev shortcut — insert one directly
into the API DB (mirrors `RegistrationTokenStore`):

```powershell
# Windows PowerShell; adjust $bin/$dbPath
$bin = 'hyveman-api\src\Hyveman.Api\bin\Debug\net10.0'
$dbPath = 'devdata\api\hyveman.db'
Add-Type -Path "$bin\SQLitePCLRaw.core.dll"; Add-Type -Path "$bin\SQLitePCLRaw.provider.e_sqlite3.dll"
Add-Type -Path "$bin\SQLitePCLRaw.batteries_v2.dll"; Add-Type -Path "$bin\Microsoft.Data.Sqlite.dll"
[SQLitePCL.Batteries]::Init()
$raw = 'reg_' + ([guid]::NewGuid().ToString('N').Substring(0, 24))
$hash = ([System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes($raw)))).Replace('-','').ToLowerInvariant()
$conn = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$dbPath"); $conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "INSERT INTO registration_tokens(id, token_hash, kind, created, created_by) VALUES ('rt_devtest', @h, 'windows-agent', @t, 'dev-mint')"
$cmd.Parameters.AddWithValue('@h', $hash) | Out-Null
$cmd.Parameters.AddWithValue('@t', (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'")) | Out-Null
$null = $cmd.ExecuteNonQuery(); $conn.Close()
Write-Output "reg token: $raw"   # put this in agent.json registration.token
```

Then run the agent as a console process (it exchanges the `reg_` token for a
long-lived `agt_` token on first contact and persists it):

```bash
dotnet run --project hyveman-agent/src/Hyveman.Agent -- --config C:/path/to/devdata/agent/agent.json
```

Notes: the console-mode Event Log source warning is harmless (only
`install.ps1` registers the event source); the PID lock in `state/` rejects a
second instance; `min_free_bytes` assumes ≥5 GiB free on the data drive.

## 5. Start the web

```bash
cd hyveman-web
NODE_EXTRA_CA_CERTS=C:/path/to/devdata/tls/dev.pem \
HYVEMAN_API_PROXY=https://127.0.0.1:8443 \
npm run dev
```

- `NODE_EXTRA_CA_CERTS` lets Vite's proxy trust the self-signed cert (without
  it you get TLS failures on `/api` calls).
- `HYVEMAN_API_PROXY` overrides the default target (`http://127.0.0.1:5080`).
- Alternative loopback shortcut (not the validated path): run the API on plain
  `http://127.0.0.1:5080` with `AllowInsecureHttp: true` — works for API+web
  only; the agent still needs HTTPS.

## 6. First-run setup + host

1. Open **http://localhost:5173** → Setup page → register the admin passkey
   (Windows Hello prompt). This exercises the full WebAuthn ceremony, which the
   API validates against the expected origin from §3.
2. Log in with the passkey.
3. **Sources** page: the agent should appear (QUASAR or your hostname) with
   live heartbeats.
4. **Hosts** page → *Add host* → name it, and **link the agent source**.
   Sources and hosts are **not** implicitly interchangeable: until a host record
   exists, the Overview correctly shows "No hosts registered" even while events
   stream in.

## 7. Verification checklist

```bash
curl -sk https://127.0.0.1:8443/health/live          # {"ok":true,...}
curl -s  http://localhost:5173/api/v1/auth/session    # via proxy: {"authenticated":false,"setupRequired":...}
# agent→API flow: the API log shows POST /ingest/telemetry 200 every ~30 s,
# POST /ingest/logs on channel activity, plus a 200 on /register right after
# the agent's first start (registration exchange).
```

Expected after ~5 minutes: events on the **Events** page (channel System has
steady DCOM-type warnings on most Windows boxes), agent status on **Sources**,
host tile on **Overview**.

## 8. What to test

- **Events**: channel/severity/event-id filters, free-text search, raw-payload
  detail panel (real raw XML renders as escaped text), URL persistence on refresh.
- **Alerts**: create a rule matching a frequent event id with a cooldown → it
  fires within a minute → acknowledge with the required reason → disable/delete.
  The seeded "Agent silent" heartbeat rule fires whenever the API restarts while
  the agent is down — expected, resolves itself.
- **Failed logons**: `runas /user:nonexistent x` in cmd, any password → Security
  4625 → appears in Events and LogonStats (curated security log pipeline).
- **Audit**: setup, login, host create/edit, rule changes all leave entries.
- **Maintenance window** on the host → new alerts during the window show as
  *silenced*.
- **Logout → login** round trip (passkey assertion path).
- **Resilience drill**: stop the API ~60 s — agent spools to disk and reports
  `degraded`; restart → drains. Web session survives (DB-backed).
- **Webhook notifications**: point a channel at a local HTTP listener
  (e.g. `node -e "require('http').createServer((q,s)=>{console.log(q.method,q.url);s.end('ok')}).listen(9099)"`)
  → trigger an alert → outbox delivers.
- **Not testable here**: iDRAC/Redfish hardware telemetry (needs a Dell BMC),
  Hyper-V VM facts (needs a Hyper-V host), Telegram/SMTP channels.

## 9. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Passkey setup: options OK, verify 500 `FormatException` | stale build before the base64url fix — rebuild the API; the challenge in `clientDataJSON` is base64url and must be decoded as such (`WebAuthnService.DecodeChallenge`) |
| Passkey ceremony rejected on origin | `--WebAuthnExpectedOrigin` must be exactly `http://localhost:5173`; browser must use `localhost`, not `127.0.0.1` |
| Vite `/api` calls fail with TLS errors | `NODE_EXTRA_CA_CERTS` missing/pointing at the wrong PEM |
| Agent: `Backend health check failed` | wrong `backend.url`/`ca_path`/hostname in cert SANs; or `validate_cert` mismatch |
| Agent: `auth_rejected` after registration | `reg_` token already consumed (response lost) — reissue |
| Agent: Event Log error at startup | console mode, event source unregistered — harmless |
| Git Bash: `C:\...` args produce junk `C:Dev...` files | bash ate the backslashes — use forward slashes or run from PowerShell |
| API listens on 5080 instead of your URL | `--data-dir` didn't reach the app (see previous row) and no `config.json` was found |

## 10. Stopping

Ctrl+C each console process (agent: kill `hyveman-agent`; API: kill
`hyveman-api`; web: kill the vite `node`). Restart order: API → agent → web.
Data persists in the data dir; nothing to clean up between sessions.
