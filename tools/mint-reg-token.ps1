# Mint a single-use registration token by inserting its SHA-256 hash directly
# into the server's SQLite DB (mirrors RegistrationTokenStore.CreateAsync).
# Intended for dev/test environments; production path is the web UI
# (INSTALL §4.5).
#
# Usage:
#   .\tools\mint-reg-token.ps1                                 # dev fallback
#   .\tools\mint-reg-token.ps1 -DataDir C:\hyveman\data        # production
#   .\tools\mint-reg-token.ps1 -DataDir C:\hyveman\data -Id rt_qa -Kind windows-agent
#
# Prints the raw reg_ token ONCE — put it in the agent's agent.json, then
# delete this output.
param(
    [string]$DbPath,
    [string]$DataDir,
    [string]$Id,
    [string]$Kind = 'windows-agent'
)
$ErrorActionPreference = 'Stop'

$tool = Join-Path $PSScriptRoot 'mint-reg-token'
$toolArgs = @()
if ($DbPath) {
    $toolArgs += '--db'; $toolArgs += $DbPath
}
elseif ($DataDir) {
    $toolArgs += '--data-dir'; $toolArgs += $DataDir
}
elseif ($env:HYVEMAN_DATA_DIR) {
    $toolArgs += '--data-dir'; $toolArgs += $env:HYVEMAN_DATA_DIR
}
if ($Id) { $toolArgs += '--id'; $toolArgs += $Id }
if ($Kind) { $toolArgs += '--kind'; $toolArgs += $Kind }

& dotnet run --project $tool --verbosity quiet -- @toolArgs
exit $LASTEXITCODE
