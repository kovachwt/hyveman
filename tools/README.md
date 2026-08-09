# Hyveman ops tools

Small, committed .NET 10 console tools for peeking at and seeding a Hyveman
server's SQLite DB — useful in the dev stack (`devdata/api/hyveman.db`) and on
a deployed server (`--data-dir C:\hyveman\data`, INSTALL §4.2).

All tools resolve the DB the same way (first hit wins):

1. `--db <path>` — explicit database file
2. `--data-dir <dir>` — `<dir>\hyveman.db` (matches the API's `--data-dir`)
3. env `HYVEMAN_DATA_DIR` — same as `--data-dir`
4. walk up from the CWD — `devdata/api/hyveman.db` dev-stack fallback

They run via `dotnet run` because Windows PowerShell 5.1 cannot host .NET 10
assemblies (the old `Add-Type` approach in `devdata/` was broken for this
reason). First invocation builds (~seconds); later ones are incremental.
Don't run two invocations against the same tool concurrently — parallel
`dotnet run` builds on one project directory collide.

## dbquery — inspect the server DB

```powershell
.\tools\query-db.ps1                                      # default inspection set
.\tools\query-db.ps1 -DataDir C:\hyveman\data             # production data dir
.\tools\query-db.ps1 "SELECT * FROM vms"                  # arbitrary SQL
.\tools\query-db.ps1 -DataDir C:\hyveman\data "SELECT name, state FROM vms"
```

No SQL → prints an ops dashboard: tables, sources, tokens, hosts,
`agent_status`, vms, latest events, latest alerts, rules, passkeys,
`web_sessions`, `logon_stats`, `audit_log`, settings, `schema_migrations`,
counts. With SQL → runs it, printing `col=val | col=val` rows. Per-query
failures print `ERROR:` and don't abort the rest of a default run.

Direct: `dotnet run --project tools/dbquery -- [--db <path> | --data-dir <dir>] [SQL...]`

## mint-reg-token — seed a registration token (dev/test only)

```powershell
.\tools\mint-reg-token.ps1                                # dev fallback
.\tools\mint-reg-token.ps1 -DataDir C:\hyveman\data       # production data dir
.\tools\mint-reg-token.ps1 -DataDir C:\hyveman\data -Id rt_qa -Kind windows-agent
```

Mirrors `RegistrationTokenStore.CreateAsync` exactly (raw `reg_` + 48 hex,
SHA-256 hash stored, never the raw value; `created` in `TimeFormat.Full`).
Prints the raw token **once** — put it in the agent's `agent.json`, then
delete the output. Fails if the `--id` already exists.

Production enrollment should use the web UI (INSTALL §4.5) — this exists for
dev stacks and scripted staging/testing setups.
