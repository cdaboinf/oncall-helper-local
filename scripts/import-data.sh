#!/usr/bin/env sh
# Load a hosted export or a backup file into the local database. Usage: ./scripts/import-data.sh <file.json>
# Shows a dry run first and asks before importing. Safe to repeat: ids are kept, so it updates instead of duplicating.
set -eu
C="${ONCALL_CONTAINER:-oncall-local-mcp}"
file="${1:-}"
if [ -z "$file" ] || [ ! -f "$file" ]; then echo "Usage: $0 <path to the .json export or backup file>" >&2; exit 1; fi
if [ "$(docker inspect -f '{{.State.Running}}' "$C" 2>/dev/null || true)" != "true" ]; then
  echo "The container '$C' is not running. Start it first: docker compose -f compose.corp.yaml up -d" >&2; exit 1
fi
cleanup() { docker exec "$C" rm -f /tmp/import.json >/dev/null 2>&1 || true; }
trap cleanup EXIT
docker cp "$file" "$C:/tmp/import.json"
echo "--- dry run (nothing is written) ---"
docker exec "$C" dotnet OnCallHelperLocal.dll import --dry-run --file /tmp/import.json
printf "Import these incidents into the local database? Type yes to continue: "
read -r answer
if [ "$answer" != "yes" ]; then echo "Cancelled. Nothing was imported."; exit 0; fi
docker exec "$C" dotnet OnCallHelperLocal.dll import --file /tmp/import.json
echo "Done. The file you imported holds real incident data: delete it when you no longer need it."
