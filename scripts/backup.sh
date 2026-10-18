#!/usr/bin/env sh
# Write a dated backup of all incidents to ./backups (a folder on this machine) and keep the newest 30.
# Usage: ./scripts/backup.sh        Restore with: ./scripts/import-data.sh backups/<file>.json
set -eu
C="${ONCALL_CONTAINER:-oncall-local-mcp}"
if [ "$(docker inspect -f '{{.State.Running}}' "$C" 2>/dev/null || true)" != "true" ]; then
  echo "The container '$C' is not running. Start it first: docker compose -f compose.corp.yaml up -d" >&2; exit 1
fi
docker exec "$C" dotnet OnCallHelperLocal.dll backup /backups --keep "${KEEP:-30}"
