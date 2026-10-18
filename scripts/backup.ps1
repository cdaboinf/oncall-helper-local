# Write a dated backup of all incidents to .\backups (a folder on this machine) and keep the newest 30.
# Usage: .\scripts\backup.ps1        Restore with: .\scripts\import-data.ps1 backups\<file>.json
$ErrorActionPreference = "Stop"
$C = if ($env:ONCALL_CONTAINER) { $env:ONCALL_CONTAINER } else { "oncall-local-mcp" }
$keep = if ($env:KEEP) { $env:KEEP } else { "30" }
$running = (docker inspect -f '{{.State.Running}}' $C 2>$null)
if ($running -ne "true") { Write-Error "The container '$C' is not running. Start it first: docker compose -f compose.corp.yaml up -d"; exit 1 }
docker exec $C dotnet OnCallHelperLocal.dll backup /backups --keep $keep
exit $LASTEXITCODE
