# Query the Hyveman server's SQLite DB (dev or production data dir).
#
# Usage:
#   .\tools\query-db.ps1                                          # default inspection set
#   .\tools\query-db.ps1 -DataDir C:\hyveman\data                 # production data dir
#   .\tools\query-db.ps1 -DbPath C:\hyveman\data\hyveman.db      # explicit file
#   .\tools\query-db.ps1 "SELECT * FROM vms"                      # arbitrary SQL (dev fallback)
#   .\tools\query-db.ps1 -DataDir C:\hyveman\data "SELECT * FROM vms"
#
# DB resolution: -DbPath > -DataDir > env HYVEMAN_DATA_DIR > devdata/api/hyveman.db
# under the CWD (dev-stack fallback). Runs the net10.0 DbQuery tool via
# `dotnet run` — Windows PowerShell 5.1 cannot host .NET 10 assemblies.
param(
    [string]$DbPath,
    [string]$DataDir,
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)][string[]]$Sql
)
$ErrorActionPreference = 'Stop'

$tool = Join-Path $PSScriptRoot 'dbquery'
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
$toolArgs += '--'
$toolArgs += $Sql

& dotnet run --project $tool --verbosity quiet -- @toolArgs
exit $LASTEXITCODE
