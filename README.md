# OnCallHelperLocal

A local, MCP-only on-call incident tracker. Claude reads a pasted Slack thread or notes and turns it into an
incident; this server stores incidents on your own machine and finds similar ones. No cloud, no API keys.

It mirrors the logic of the AWS app (`oncall-assistant-api`) but is a separate project: that app is built for AWS
and is not changed by this one.

## How it fits together
```
Claude  --docker exec -i-->  oncall-local-mcp  --->  oncall-local-mongodb   (MongoDB Atlas Local, vector search)
(MCP over stdio)                 (.NET app)    --->  oncall-local-ollama    (nomic-embed-text, 768 dimensions)
```
- **Claude does the reading and reasoning.** It extracts the incident from the chat, decides whether a past incident
  really matches, and writes the summary. This server has no language model of its own.
- **What is searchable** is each incident's **title, description, root cause and resolution summary**, embedded together
  (embedding version 2). That is a deliberate difference from the AWS app, which embeds only the description: here a
  question about *how something was fixed* can match the fix. A raw chat is never embedded; the description is limited
  to 6,000 characters, so it must be a short summary.
- **No ports are published.** Nothing outside Docker can reach the database or the embedding service, so they have no
  passwords. Claude starts the MCP server on demand inside the container.
- The MongoDB document shape is the same as the AWS app's, but the vectors differ (different model and different text),
  so a transfer needs re-embedding. `EmbeddingVersion` records which recipe made a vector (1 = AWS, 2 = this app).

## Tools
| Tool | Kind | Purpose |
|---|---|---|
| `find_similar_incidents` | read | Semantic search from a short summary; returns incidents with similarity scores |
| `search_incidents` | read | Keyword and filter search (service, severity, environment, dates), newest first. With a text query it **also returns the closest incidents by meaning** (`semanticMatches`), so a keyword search can never end in a bare "nothing found" |
| `create_incident` | write | Save a new incident (resolution optional while it is open) |
| `update_incident` | write | Change fields of an incident, typically add the resolution; unspecified fields keep their values |

Scores are relative similarity (roughly 0.5 to 1). Compare results and read them; there is no reliable fixed cut-off.

## Requirements
- Docker Desktop (Windows, Mac or Linux) with at least about 4 GB of memory available to containers.
  Docker Desktop's licence terms depend on your organisation; check yours.
- About 10 GB of free disk for the images and the embedding model (Ollama's image is about 7 GB).
- The first start needs internet access to download the images and the model.

## Start
```
docker compose up -d --build
docker logs -f oncall-local-mcp
```
The first start downloads the images and the model (several minutes). The app container waits for the database and
the embedding service, then prints `vector index 'vector_index' ready ...`. After that it stays running idle; Claude
starts the MCP server inside it when needed.

Stop and start later with `docker compose stop` and `docker compose start`. Data is kept in Docker volumes.

## Connect Claude
Add this local MCP server to Claude Desktop's config. Quit Claude Desktop completely first, because it can overwrite
the file while running. Settings > Developer > Edit config opens the file; it is normally
`%APPDATA%\Claude\claude_desktop_config.json` on Windows and
`~/Library/Application Support/Claude/claude_desktop_config.json` on a Mac.
```json
{
  "mcpServers": {
    "oncall-helper-local": {
      "command": "docker",
      "args": ["exec", "-i", "oncall-local-mcp", "dotnet", "OnCallHelperLocal.dll"]
    }
  }
}
```
Add `"oncall-helper-local"` alongside any servers you already have, then reopen Claude Desktop. The docker containers
must be running (Docker Desktop started, `docker compose start`).

## Using it
Paste or attach a Slack conversation and ask, for example:
> Extract the incident from this chat, check whether we already have a similar one and tell me how it was resolved. If
> none matches, show me a draft and wait for my OK before saving it.

Later: *"Update that incident with the resolution: ..."*. The server instructions tell Claude to ask before every
write and to keep code, copied wiki text, secrets and personal data out of what it saves.

## Copy incidents from the hosted app
Two steps: export from the hosted API, then import into the local database. The hosted app's vectors come from a
different model, so every description is embedded again locally; ids, dates and resolutions are kept, and running the
import again updates instead of duplicating.

1. **Export** (on a machine that can reach the hosted API). Give it credentials through environment variables so no secret
   is typed into a command (zsh):
   ```
   export ONCALL_AUTH0_DOMAIN=your-tenant.us.auth0.com
   read -s "ONCALL_AUTH0_CLIENT_ID?M2M client id: "; export ONCALL_AUTH0_CLIENT_ID
   read -s "ONCALL_AUTH0_CLIENT_SECRET?M2M client secret: "; export ONCALL_AUTH0_CLIENT_SECRET
   python3 tools/export_hosted.py
   ```
   (`ONCALL_API_BASE_URL` defaults to `https://api.oncallhelper.com`; `ONCALL_API_TOKEN` can replace the three login values.)
   It writes `exports/hosted-incidents-<time>.export.json`, readable only by you, and prints counts only.
2. **Preview, then import** (the file goes in through stdin because the database has no published port):
   ```
   docker exec -i oncall-local-mcp dotnet OnCallHelperLocal.dll import --dry-run < exports/<file>.export.json
   docker exec -i oncall-local-mcp dotnet OnCallHelperLocal.dll import          < exports/<file>.export.json
   ```
   On Windows PowerShell use `Get-Content -Raw exports\<file>.export.json | docker exec -i oncall-local-mcp dotnet OnCallHelperLocal.dll import`.
3. **Delete the export file.** It holds real incident data and is ignored by Git (`exports/`), but it is still a copy.

The import skips incidents with no description or an invalid id and reports them. Descriptions longer than 4,000
characters are stored in full but embedded from their first 4,000 characters. A bulk import can take a short while to
become searchable by similarity.

`python tests/migration_test.py` checks the whole pipeline with made-up data only.

## Re-embedding (`reembed`)
If incidents were saved with an older embedding recipe (for example, imported before version 2 existed), rebuild their
vectors from the text already stored; no export file is needed:
```
docker exec oncall-local-mcp dotnet OnCallHelperLocal.dll reembed --dry-run   # counts only
docker exec oncall-local-mcp dotnet OnCallHelperLocal.dll reembed             # rebuilds the out-of-date ones
```
`--all` rebuilds every incident. Changing the embedding model also needs a new index (see Limits).

## How Claude is told to search
The server instructions ask Claude to search with two or three phrasings and merge the results, to try word variants for
keyword search, to judge by the gap between the best score and the rest, to lead with a one-line verdict, and to ask at
most one clarifying question when a question is vague.

## Check that it works
```
python tests/smoke_test.py
```
Uses only made-up incidents and a separate test database (`oncall_local_test`, dropped afterwards). Set
`ONCALL_CONTAINER=<name>` to run it against another container with the same image.

## Data, backup, reset
- Data lives in the Docker volumes `oncall-helper-local_db`, `_configdb`, `_mongot` and the model in `_ollama`.
- Backup (consistent only while stopped): `docker compose stop`, then copy the volume, for example
  `docker run --rm -v oncall-helper-local_db:/data -v "%cd%":/backup alpine tar czf /backup/db.tgz -C /data .`
  (use `${PWD}` in PowerShell, `$(pwd)` in a Mac or Linux shell).
- Reset everything, including the downloaded model: `docker compose down -v`.

## Limits worth knowing
- MongoDB Atlas Local is documented as for local development and testing, not production, and its `preview` tag moves.
  Pin a version or digest once you have verified one.
- `nomic-embed-text` reads about the first 2,048 tokens of a text, which is why descriptions are summaries. Scores come
from Atlas as `(1 + cosine) / 2`, so they sit between 0.5 and 1 and bunch together when incidents are alike.
- Search quality was checked only on made-up data. Try it on your own incidents before relying on it.
- If you change the embedding model, the vector index and every stored vector must be rebuilt.

## Troubleshooting
| Symptom | Likely cause |
|---|---|
| Tool error: "vector index is missing or not ready" | Setup has not finished. `docker logs oncall-local-mcp`, or run `docker exec oncall-local-mcp dotnet OnCallHelperLocal.dll setup` |
| "embedding service is not reachable" or "model ... may not be downloaded" | Ollama is still starting or downloading: `docker logs oncall-local-ollama`; model name `nomic-embed-text` |
| Claude shows the server as failed | Docker Desktop is not running or the containers are stopped (`docker compose start`) |
| `search_incidents` works, `find_similar_incidents` is empty | No incidents saved yet, or they were saved while the embedding service was down |

## Development
```
dotnet build OnCallHelperLocal
```
Settings come from environment variables (`MONGO_CONNECTION_STRING`, `MONGO_DATABASE`, `EMBEDDINGS_BASE_URL`,
`EMBEDDINGS_MODEL`, `EMBEDDINGS_DIMENSIONS`); the defaults match `compose.yaml`. See `CLAUDE.md` for the working rules.
