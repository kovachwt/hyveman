#requires -Version 5.1
<#
.SYNOPSIS
    Installs the hyveman-agent Windows service (AGENT.md §11.2). Idempotent — re-run is safe.

.DESCRIPTION
    One-liner bootstrap for a host:
      ./install.ps1 -BackendUrl https://hyveman.example.lan:8443 -InstallToken reg_xxx [-EnableHyperV]

    Steps: dirs + ACLs, exe copy, agent.json, Hyper-V channels (opt-in), event-log
    source, SCM service with recovery, preflight (fail closed), start.

    Re-runs preserve an existing, valid agent.json (it holds the exchanged ingest
    token and any operator edits); delete agent.json to regenerate from scratch.

.PARAMETER BackendUrl
    Backend base URL (https only, no trailing slash).

.PARAMETER InstallToken
    One-time admin-issued registration token (reg_...). Exchanged for the long-lived
    ingest token on first agent contact (PROTOCOL §5); never stored after.

.PARAMETER ExePath
    Path to hyveman-agent.exe to deploy. Defaults to .\hyveman-agent.exe.

.PARAMETER DataDir
    Single data directory (DESIGN §9). Default C:\ProgramData\hyveman-agent.

.PARAMETER EnableHyperV
    Enable the Hyper-V operational channels (installer-only channel change, §11.2 step 4)
    and add the Hyper-V channel set to agent.json.

.PARAMETER SkipPreflight
    Bypass the network/cert reachability preflight (lab use).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackendUrl,
    [Parameter(Mandatory = $true)]
    [string]$InstallToken,
    [string]$ExePath = (Join-Path $PSScriptRoot "hyveman-agent.exe"),
    [string]$DataDir = "C:\ProgramData\hyveman-agent",
    [switch]$EnableHyperV,
    [switch]$SkipPreflight
)

$ErrorActionPreference = "Stop"
$InstallDir = "C:\Program Files\hyveman-agent"
$ServiceName = "hyveman-agent"
$ExeName = "hyveman-agent.exe"

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Assert($cond, $msg) { if (-not $cond) { throw $msg } }

function Invoke-Native {
    param([string]$FilePath, [string[]]$Arguments)
    # PS 5.1 gotcha: with $ErrorActionPreference=Stop, ANY stderr from a native
    # command becomes a terminating error even when redirected (2>$null). Drop
    # EAP around the call and judge success by exit code alone — the callers
    # decide warn-vs-abort (AGENT §11.3: missing Hyper-V channels must not
    # abort the install).
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $FilePath @Arguments 2>$null | Out-Null
        return $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prev
    }
}

# ---------------------------------------------------------------------------
Write-Step "Preflight (fail closed, AGENT §11.3)"

$os = Get-CimInstance Win32_OperatingSystem
$build = [int]$os.BuildNumber
Assert ($build -ge 17763) "OS too old: Windows Server 2019 (build 17763) or Windows 10 1809+ required (this host: build $build)"

Assert (-not [string]::IsNullOrWhiteSpace($InstallToken)) "-InstallToken is required"
Assert ($InstallToken.StartsWith("reg_")) "-InstallToken must start with 'reg_'"
Assert ($BackendUrl.StartsWith("https://") -or $BackendUrl.StartsWith("http://")) "-BackendUrl must be an http(s) URL"
Assert (Test-Path $ExePath) "hyveman-agent.exe not found at $ExePath (build it with build.ps1 first)"

$spoolPath = Join-Path $DataDir "spool"
if (-not (Test-Path $DataDir)) { New-Item -ItemType Directory -Force -Path $DataDir | Out-Null }
$qualifier = Split-Path $DataDir -Qualifier
if ($qualifier -match '^[A-Za-z]:$') {
    $drive = Get-PSDrive $qualifier.TrimEnd(':')
    $freeGb = [math]::Round($drive.Free / 1GB, 1)
    Write-Step "Spool volume free: ${freeGb} GB"
    Assert ($drive.Free -gt 5GB) "Spool volume must have >= 5 GiB free (spool.min_free_bytes floor)"
} else {
    Write-Host "WARNING: cannot determine free space for '$DataDir' (UNC or relative path) — skipping the 5 GiB spool-volume check." -ForegroundColor Yellow
}

# Audit policy: curated Security events need "Audit Logon Events" (warning, not abort).
try {
    $audit = auditpol /get /subcategory:"Logon" 2>$null
    if ($audit -match "Success and Failure|Success") {
        Write-Step "Audit Logon Events: enabled (curated Security log will work)"
    } else {
        Write-Host "WARNING: 'Audit Logon Events' policy is not enabled — curated Security events (4624/4625/4740) will be empty." -ForegroundColor Yellow
        Write-Host "         Enable with: auditpol /set /subcategory:'Logon' /success:enable /failure:enable"
    }
} catch { Write-Host "WARNING: could not check audit policy" -ForegroundColor Yellow }

if (-not $SkipPreflight) {
    $uri = [uri]$BackendUrl
    Write-Step "Checking backend reachability $($uri.Host):$($uri.Port) (TCP)"
    $tcp = New-Object System.Net.Sockets.TcpClient
    try {
        $async = $tcp.BeginConnect($uri.Host, $uri.Port, $null, $null)
        Assert ($async.AsyncWaitHandle.WaitOne(5000)) "Backend $BackendUrl not reachable (TCP connect timed out)"
        $tcp.EndConnect($async)
    } finally { $tcp.Close() }
    Write-Step "Backend reachable."
} else {
    Write-Host "WARNING: preflight network check skipped (-SkipPreflight)" -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
Write-Step "Create data dirs + ACLs (SYSTEM + Administrators; deny Users)"

$dirs = @($DataDir, $spoolPath, (Join-Path $DataDir "state"), (Join-Path $DataDir "logs"), (Join-Path $DataDir "state\quarantine"))
foreach ($d in $dirs) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

# Data dir ACL: SYSTEM + Administrators full; Users denied. (Token lives in agent.json.)
$acl = Get-Acl $DataDir
$acl.SetAccessRuleProtection($true, $false)   # drop inherited (Users)
$sysRule = New-Object System.Security.AccessControl.FileSystemAccessRule("SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$admRule = New-Object System.Security.AccessControl.FileSystemAccessRule("Administrators", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.AddAccessRule($sysRule)
$acl.AddAccessRule($admRule)
Set-Acl $DataDir $acl
Get-ChildItem $DataDir -Directory | ForEach-Object {
    $a = Get-Acl $_.FullName
    $a.SetAccessRuleProtection($false, $true)  # inherit the hardened root
    Set-Acl $_.FullName $a
}

# ---------------------------------------------------------------------------
Write-Step "Deploy exe"

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $ExePath (Join-Path $InstallDir $ExeName) -Force

# ---------------------------------------------------------------------------
Write-Step "Write agent.json (bootstrap config)"

# Default channel set (AGENT App. B). Hyper-V channels added only with -EnableHyperV.
$channels = @(
    @{ name = "System";        level = "Warning" },
    @{ name = "Application";   level = "Warning" },
    @{ name = "Security";      level = "Warning" },   # curated IDs via security_log
    @{ name = "HyvemanAgent";  channel = "Application"; provider = "HyvemanAgent"; level = "Information"; include_ids = @(1,2,3,4,5) }
)

# Hyper-V channel set (AGENT App. B). Probing: channels are auto-detected —
# e.g. High-Availability-Admin only exists on clustered hosts — so each is
# checked with `wevtutil gl` before being added to agent.json (omitted with a
# warning if absent; the agent skips configured-but-missing channels anyway).
$hypervChannels = @(
    @{ name = "Microsoft-Windows-Hyper-V-VMMS-Admin";            level = "Warning" },
    @{ name = "Microsoft-Windows-Hyper-V-Worker-Admin";          level = "Warning" },
    @{ name = "Microsoft-Windows-Hyper-V-Compute-Operational";   level = "Warning" },
    @{ name = "Microsoft-Windows-Hyper-V-Config-Operational";    level = "Information" },
    @{ name = "Microsoft-Windows-Hyper-V-StorageVSP-Admin";      level = "Warning" },
    @{ name = "Microsoft-Windows-Hyper-V-High-Availability-Admin"; level = "Warning" },
    @{ name = "Microsoft-Windows-Hyper-V-Image-Management-Operational"; level = "Information" }
)
$hypervPresent = @()

if ($EnableHyperV) {
    foreach ($def in $hypervChannels) {
        if ((Invoke-Native "wevtutil" @("gl", $def.name)) -eq 0) {
            $channels += $def
            $hypervPresent += $def.name
        } else {
            Write-Host "WARNING: Hyper-V channel $($def.name) not readable on this host — omitted from agent.json" -ForegroundColor Yellow
        }
    }
}

$config = @{
    backend = @{
        url           = $BackendUrl.TrimEnd('/')
        token         = $null            # filled by POST /register on first contact
        validate_cert = $true
    }
    spool = @{
        dir            = $spoolPath
        max_bytes      = 104857600       # 100 MiB
        min_free_bytes = 5368709120      # 5 GiB
    }
    limits = @{
        process_memory_bytes = 268435456 # 256 MiB job-object kill cap
        cpu_rate_percent     = 25
        in_memory_queue_events = 10000
        batch_max_events     = 500
        batch_max_age_ms     = 1000
        max_batch_bytes      = 4194304
        max_raw_bytes        = 8192
        send_concurrency     = 2
        send_timeout_ms      = 30000
        gzip                 = $true
    }
    wmi = @{ scan_interval_s = 60; query_timeout_s = 20; max_queries_per_scan = 8 }
    heartbeat = @{ interval_s = 30 }
    security_log = @{
        enabled             = $true
        include_ids         = @(4624, 4625, 4740)
        logon_types_for_4624 = @(2, 10)
    }
    channels = $channels
    logging = @{ level = "Information"; dir = (Join-Path $DataDir "logs"); rolling = "10MBx5" }
    data_dir = $DataDir
    registration = @{
        token         = $InstallToken   # one-time; discarded after exchange
        kind          = "windows-agent"
        agent_version = $null
        os_build      = "$build"
    }
}

$configPath = Join-Path $DataDir "agent.json"
$deployedExe = Join-Path $InstallDir $ExeName
if (Test-Path $configPath) {
    # A valid existing agent.json is authoritative: after first contact it holds the
    # long-lived ingest token (agt_...) exchanged for the one-time reg_ token, plus
    # any operator edits (channels, limits). Overwriting it would break agent auth
    # and silently discard those edits — so preserve it (server installers do the
    # same). Regenerate only when it fails validation (with a backup kept).
    $prevEap = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    & $deployedExe --config $configPath --validate-config 2>$null
    $validateCode = $LASTEXITCODE
    $ErrorActionPreference = $prevEap
    if ($validateCode -eq 0) {
        Write-Step "agent.json exists and validates — preserving it (keeps the exchanged ingest token and operator edits)."
        Write-Host "         To regenerate from scratch: delete $configPath and re-run." -ForegroundColor Yellow
    } else {
        Write-Host "WARNING: agent.json exists but fails validation — backing it up and regenerating." -ForegroundColor Yellow
        Copy-Item $configPath "$configPath.bak.$(Get-Date -Format yyyyMMddHHmmss)" -Force
        $config | ConvertTo-Json -Depth 10 | Set-Content -Path $configPath -Encoding UTF8
    }
} else {
    $config | ConvertTo-Json -Depth 10 | Set-Content -Path $configPath -Encoding UTF8
}

# ---------------------------------------------------------------------------
Write-Step "Enable Hyper-V operational channels (installer-only; §11.2 step 4)"

if ($EnableHyperV) {
    foreach ($ch in $hypervPresent) {
        if ((Invoke-Native "wevtutil" @("sl", $ch, "/e:true")) -eq 0) { Write-Step "  enabled $ch" }
        else { Write-Host "  WARNING: could not enable $ch (may not exist on this host)" -ForegroundColor Yellow }
    }
    # Marker for uninstall (only remove channels we enabled).
    @{ channels = $hypervPresent } | ConvertTo-Json | Set-Content (Join-Path $DataDir "state\hyperv-channels-enabled.json")
}

# ---------------------------------------------------------------------------
Write-Step "Register EventLog source HyvemanAgent (installer-only registry write; AGENT §12)"

if (-not [System.Diagnostics.EventLog]::SourceExists("HyvemanAgent")) {
    New-EventLog -LogName Application -Source "HyvemanAgent" -ErrorAction Stop
}

# ---------------------------------------------------------------------------
Write-Step "Create/update service (recovery: 3 restarts / 4 h then STOP, AGENT §4.3)"

$binPath = "`"$(Join-Path $InstallDir $ExeName)`" --service"

$svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $svc) {
    sc.exe create $ServiceName binPath= $binPath start= delayed-auto | Out-Null
    Assert ($LASTEXITCODE -eq 0) "sc create failed (exit $LASTEXITCODE)"
} else {
    sc.exe config $ServiceName binPath= $binPath | Out-Null
    sc.exe config $ServiceName start= delayed-auto | Out-Null
}
sc.exe description $ServiceName "Hyveman log & health agent" | Out-Null
sc.exe failure $ServiceName reset= 14400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
sc.exe config $ServiceName start= delayed-auto | Out-Null

# ---------------------------------------------------------------------------
Write-Step "Validate config with the real binary (fail closed)"

$prevEap = $ErrorActionPreference; $ErrorActionPreference = "Continue"
& (Join-Path $InstallDir $ExeName) --config $configPath --validate-config
$validateCode = $LASTEXITCODE
$ErrorActionPreference = $prevEap
Assert ($validateCode -eq 0) "agent config validation failed — aborting before service start (roll back: run uninstall.ps1)"

# ---------------------------------------------------------------------------
Write-Step "Start service"

Start-Service $ServiceName
Write-Step "Done. hyveman-agent installed as '$ServiceName' (delayed-auto)."
Write-Host "  - Config:    $configPath"
Write-Host "  - Data:      $DataDir"
Write-Host "  - Logs:      $(Join-Path $DataDir 'logs')"
if ($EnableHyperV) { Write-Host "  - Hyper-V channels enabled (marked for uninstall cleanup)" }
Write-Host "  - On first start the agent exchanges the reg_ token for an ingest token via POST /register (PROTOCOL §5)."
Write-Host "  - Uninstall: ./uninstall.ps1"
