<#
.SYNOPSIS
    Hyveman server installer (SERVER.md §15.1): copies the exe, creates the data dir with
    correct ACLs, registers the Windows service, and starts it.

.DESCRIPTION
    One-liner:
      powershell -ExecutionPolicy Bypass -File install.ps1

    Options:
      -ExePath   <path>       path to hyveman-server.exe (default: alongside this script)
      -DataDir   <path>       data directory (default: %ProgramData%\Hyveman\server)
      -Port      <int>        HTTPS port (default: 443); ignored if server.json exists
      -CertPath  <path>       TLS .pfx (default: data dir config\cert.pfx if present)
      -LetsEncrypt <email>    enable automatic Let's Encrypt certificates (ACME http-01);
                              requires at least one -Domain; port 80 must be reachable from
                              the internet on the server's public IP
      -Domain    <name>       public DNS name for the certificate (repeatable)
      -HttpPort  <int>        http-01 challenge listener port (default: 80)
      -NoStart               register the service but do not start it
      -Uninstall             stop + delete the service (keeps data dir)

    Idempotent: re-running updates the service and preserves the data dir.
#>
[CmdletBinding()]
param(
    [string]$ExePath = "",
    [string]$DataDir = "$env:ProgramData\Hyveman\server",
    [int]$Port = 443,
    [string]$CertPath = "",
    [string]$LetsEncrypt = "",
    [string[]]$Domain = @(),
    [int]$HttpPort = 80,
    [switch]$NoStart,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$ServiceName = "HyvemanServer"
$InstallDir = "$env:ProgramFiles\Hyveman\server"

if ($Uninstall) {
    Write-Host "Stopping and removing service $ServiceName (data dir preserved)..." -ForegroundColor Yellow
    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
        sc.exe delete $ServiceName | Out-Null
    }
    Write-Host "Done. Data dir: $DataDir" -ForegroundColor Green
    exit 0
}

if ($LetsEncrypt -and $Domain.Count -eq 0) {
    Write-Error "-LetsEncrypt requires at least one -Domain (public DNS names for the certificate)."
    exit 1
}
if ($CertPath -and $LetsEncrypt) {
    Write-Error "-CertPath and -LetsEncrypt are mutually exclusive."
    exit 1
}

# ── elevation ──────────────────────────────────────────────────────────────
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "install.ps1 must run elevated (Administrator)."
    exit 1
}

# ── files ──────────────────────────────────────────────────────────────────
if (-not $ExePath) { $ExePath = Join-Path $PSScriptRoot "hyveman-server.exe" }
if (-not (Test-Path $ExePath)) {
    Write-Error "hyveman-server.exe not found at $ExePath. Publish first: dotnet publish -c Release"
    exit 1
}

Write-Host "Installing Hyveman server..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $ExePath (Join-Path $InstallDir "hyveman-server.exe") -Force

# ── data dir + ACLs (SYSTEM + Administrators) ──────────────────────────────
New-Item -ItemType Directory -Force -Path "$DataDir\config", "$DataDir\backup", "$DataDir\logs" | Out-Null
$acl = Get-Acl $DataDir
$acl.SetAccessRuleProtection($true, $false)
$admins = New-Object System.Security.Principal.SecurityIdentifier(
    [System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
$system = New-Object System.Security.Principal.SecurityIdentifier(
    [System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
foreach ($sid in @($admins, $system)) {
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $sid, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.AddAccessRule($rule)
}
Set-Acl $DataDir $acl

# ── default config (only if absent; never overwrite operator config) ───────
$configPath = "$DataDir\config\server.json"
if (-not (Test-Path $configPath)) {
    $config = @{
        urls = "https://0.0.0.0:$Port"
        tls  = if ($LetsEncrypt) {
            @{
                min_tls        = "1.2"
                preferred_tls  = "1.3"
                lets_encrypt   = @{
                    enabled    = $true
                    domains    = @($Domain)
                    email      = $LetsEncrypt
                    staging    = $false
                    renew_days = 30
                    http_port  = $HttpPort
                }
            }
        } else {
            $pfx = $CertPath
            if (-not $pfx) {
                $candidate = "$DataDir\config\cert.pfx"
                if (Test-Path $candidate) { $pfx = $candidate }
            }
            @{
                cert_path      = if ($pfx) { $pfx } else { "" }
                cert_password  = ""
                min_tls        = "1.2"
                preferred_tls  = "1.3"
            }
        }
        ingest = @{
            max_batch_bytes = 4194304
            max_items       = 1000
            max_raw_bytes   = 16384
            max_message_bytes = 65536
            max_field_bytes = 65536
            max_record_id_len = 128
            per_source_rate = @{ requests_per_min = 120; bytes_per_min = 33554432 }
            global_rate     = @{ requests_per_min = 1200 }
        }
        poller  = @{ interval_s = 60; timeout_s = 15; concurrency = 4 }
        alerts  = @{ sweep_s = 10; default_heartbeat_miss_s = 180 }
        notifications = @{ webhook = @{ allow_private = $false; allowed_hosts = @() } }
        retention = @{
            events_days = 365; metrics_days = 365; health_snapshots_days = 365
            audit_days = 730; resolved_alerts_days = 730; vacuum_after_purge = $true
        }
        backup = @{ time_local = "03:00"; keep_daily = 7; keep_weekly = 4; keep_monthly = 12 }
        web    = @{ session_days = 14 }
        logging = @{ level = "Information"; file_retain_days = 14 }
    }
    $config | ConvertTo-Json -Depth 6 | Set-Content -Path $configPath -Encoding utf8
    Write-Host "Wrote default $configPath" -ForegroundColor Yellow
    if ($LetsEncrypt) {
        Write-Host "Let's Encrypt enabled for: $($Domain -join ', ') (challenge listener on port $HttpPort)" -ForegroundColor Cyan
        Write-Host "  Port $HttpPort must be reachable from the internet on the server's public IP" -ForegroundColor Cyan
        Write-Host "  (and the DNS records for those names must point at this machine)." -ForegroundColor Cyan
    } elseif (-not $pfx) {
        Write-Warning "No TLS certificate configured. Set tls.cert_path in $configPath"
        Write-Warning "  (or drop a .pfx at $DataDir\config\cert.pfx, or re-run with -LetsEncrypt <email> -Domain <name>) before starting."
    }
}

# ── service registration ───────────────────────────────────────────────────
$exe = Join-Path $InstallDir "hyveman-server.exe"
$binPath = "`"$exe`" --data-dir `"$DataDir`""
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Service $ServiceName exists — updating..." -ForegroundColor Yellow
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe config $ServiceName binPath= $binPath | Out-Null
    sc.exe config $ServiceName start= auto | Out-Null
} else {
    sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= "Hyveman Server" | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Error "sc.exe create failed (exit $LASTEXITCODE)"; exit 1 }
}

if (-not $NoStart) {
    Start-Service $ServiceName
    Write-Host "Service started. Health check: https://localhost:$Port/health (X-Hyveman-Protocol: 1)" -ForegroundColor Green
} else {
    Write-Host "Service registered (not started)." -ForegroundColor Green
}

Write-Host "Data dir: $DataDir" -ForegroundColor Green
Write-Host "Next: open the web UI, run the first-run passkey wizard, then register hosts." -ForegroundColor Green
