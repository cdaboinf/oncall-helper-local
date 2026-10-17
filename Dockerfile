# Builds for the host's own CPU (amd64 on a Windows PC, arm64 on a Mac), so no platform is pinned.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY OnCallHelperLocal/OnCallHelperLocal.csproj OnCallHelperLocal/
RUN dotnet restore OnCallHelperLocal/OnCallHelperLocal.csproj
COPY OnCallHelperLocal/ OnCallHelperLocal/
RUN dotnet publish OnCallHelperLocal/OnCallHelperLocal.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app .
# Create the vector index once the database and the embedding service are up, then stay running:
# Claude starts the MCP server on demand with `docker exec -i`.
ENTRYPOINT ["/bin/sh", "-c", "dotnet OnCallHelperLocal.dll setup --wait; exec sleep infinity"]
