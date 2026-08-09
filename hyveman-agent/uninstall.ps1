#requires -Version 5.1
<#
.SYNOPSIS
    Removes the hyveman-agent service (AGENT.md §11.4). Idempotent.

.PARAMETER KeepData
    Retain spool/state/logs under the data dir for forensics (default: delete).

.PARAMETER DataDir
    Data directory to clean. Default C:\ProgramData\hyveman-agent.
#>
[CmdletBinding()]
param(
    [switch]$KeepData,
    [string]$DataDir = "C:\ProgramData\hyveman-agent"
)

$ErrorActionPreference = "Stop"
$ServiceName = "hyveman-agent"
$InstallDir = "C:\Program Files\hyveman-agent"

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

function Invoke-Native {
    param([string]$FilePath, [string[]]$Arguments)
    # PS 5.1 gotcha: with $ErrorActionPreference=Stop, ANY stderr from a native
    # command becomes a terminating error even when redirected (2>$null). Drop
    # EAP around the call and judge success by exit code alone.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $FilePath @Arguments 2>$null | Out-Null
        return $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prev
    }
}

Write-Step "Stop + delete service"
$svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $svc) {
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Write-Step "Service '$ServiceName' deleted."
} else {
    Write-Step "Service not present; nothing to delete."
}

Write-Step "Remove Hyper-V channels only if the installer enabled them (§11.4)"
$marker = Join-Path $DataDir "state\hyperv-channels-enabled.json"
if (Test-Path $marker) {
    $enabled = (Get-Content $marker | ConvertFrom-Json).channels
    foreach ($ch in $enabled) {
        # Leave the channel alone if something else is using it.
        if ((Invoke-Native "wevtutil" @("gl", $ch)) -eq 0) {
            Invoke-Native "wevtutil" @("sl", $ch, "/e:false") | Out-Null
            Write-Step "  disabled $ch (left in place, disabled)"
        }
    }
} else {
    Write-Step "No channel marker found; leaving Hyper-V channels untouched."
}

if (-not $KeepData) {
    Write-Step "Remove data dir $DataDir"
    Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Write-Step "Keeping data dir $DataDir (-KeepData)"
}

Write-Step "Remove install dir $InstallDir"
Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Step "Done."
