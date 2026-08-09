<#
.SYNOPSIS
    Publishes hyveman-agent as a single-file self-contained exe (AGENT.md §11.1).
    No PublishTrimmed (wevtapi PInvoke + WMI reflection are trim-hostile).

    Output: src\Hyveman.Agent\bin\Release\net10.0\win-x64\publish\hyveman-agent.exe
    Then:   ./install.ps1 -BackendUrl https://... -InstallToken reg_... [-EnableHyperV]
#>
$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path (Join-Path $root "src\Hyveman.Agent\Hyveman.Agent.csproj"))) {
    $root = $PSScriptRoot
}

Write-Host "==> dotnet publish (win-x64, self-contained single file)"
dotnet publish (Join-Path $root "src\Hyveman.Agent\Hyveman.Agent.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -o (Join-Path $root "out")

if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $root "out\hyveman-agent.exe"
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "==> Published: $exe (${size} MB self-contained)"
} else {
    throw "publish output not found"
}
