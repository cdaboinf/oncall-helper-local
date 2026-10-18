# Load a hosted export or a backup file into the local database. Usage: .\scripts\import-data.ps1 <file.json>
# Shows a dry run first and asks before importing. Safe to repeat: ids are kept, so it updates instead of duplicating.
param([Parameter(Mandatory = $true)][string]$File)
$ErrorActionPreference = "Stop"
$C = if ($env:ONCALL_CONTAINER) { $env:ONCALL_CONTAINER } else { "oncall-local-mcp" }
if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { Write-Error "File not found: $File"; exit 1 }
$running = (docker inspect -f '{{.State.Running}}' $C 2>$null)
if ($running -ne "true") { Write-Error "The container '$C' is not running. Start it first: docker compose -f compose.corp.yaml up -d"; exit 1 }
try {
    docker cp (Resolve-Path -LiteralPath $File).Path "${C}:/tmp/import.json"
    if ($LASTEXITCODE -ne 0) { throw "Could not copy the file into the container." }
    Write-Host "--- dry run (nothing is written) ---"
    docker exec $C dotnet OnCallHelperLocal.dll import --dry-run --file /tmp/import.json
    if ($LASTEXITCODE -ne 0) { throw "The dry run failed; nothing was imported." }
    $answer = Read-Host "Import these incidents into the local database? Type yes to continue"
    if ($answer -ne "yes") { Write-Host "Cancelled. Nothing was imported."; exit 0 }
    docker exec $C dotnet OnCallHelperLocal.dll import --file /tmp/import.json
    if ($LASTEXITCODE -ne 0) { throw "The import reported failures; see the messages above." }
    Write-Host "Done. The file you imported holds real incident data: delete it when you no longer need it."
}
finally {
    docker exec $C rm -f /tmp/import.json 2>$null | Out-Null
}
