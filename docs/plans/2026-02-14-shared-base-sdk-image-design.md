# Shared Base SDK Image for Docker Build Performance

## Problem

All 8 service Dockerfiles (Agent, WebChat, 6 MCP servers) independently copy, restore,
and compile Domain + Infrastructure from scratch. On a no-cache build, Domain +
Infrastructure are compiled 8 times in parallel, wasting CPU time and memory.
Infrastructure is particularly heavy with dependencies like Playwright, Azure ServiceBus,
Redis, Telegram, etc.

## Solution

Create a shared `base-sdk` Docker image containing pre-restored and pre-compiled Domain +
Infrastructure. Each service Dockerfile inherits from this image instead of the raw
`dotnet/sdk:10.0`, skipping redundant compilation via MSBuild incremental build detection.

## Design

### New file: `Dockerfile.base-sdk`

Builds Domain + Infrastructure into a tagged SDK image:

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /src

COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "Infrastructure/Infrastructure.csproj"

COPY ["Domain/", "Domain/"]
COPY ["Infrastructure/", "Infrastructure/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet build "Infrastructure/Infrastructure.csproj" -c Release --no-restore
```

Infrastructure depends on Domain, so restoring/building Infrastructure covers both
projects. The image contains `/src/Domain/` and `/src/Infrastructure/` with compiled
`bin/` and `obj/` output. NuGet packages live in the BuildKit cache mount (shared across
all builds on the same Docker daemon).

### Changes to each service Dockerfile

Each service Dockerfile changes minimally. Using McpServerText as the example:

**Before:**
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dependencies
WORKDIR /src
COPY ["McpServerText/McpServerText.csproj", "McpServerText/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
RUN dotnet restore "McpServerText/McpServerText.csproj"

FROM dependencies AS publish
COPY ["Domain/", "Domain/"]
COPY ["Infrastructure/", "Infrastructure/"]
COPY ["McpServerText/", "McpServerText/"]
RUN dotnet publish ...
```

**After:**
```dockerfile
FROM base-sdk:latest AS dependencies
COPY ["McpServerText/McpServerText.csproj", "McpServerText/"]
RUN dotnet restore "McpServerText/McpServerText.csproj"

FROM dependencies AS publish
COPY ["McpServerText/", "McpServerText/"]
RUN dotnet publish ...
```

Changes per Dockerfile:
1. `FROM dotnet/sdk:10.0 AS dependencies` becomes `FROM base-sdk:latest AS dependencies`
2. Remove COPY of Domain.csproj and Infrastructure.csproj (already in base)
3. Remove `WORKDIR /src` (already set in base)
4. Remove COPY of `Domain/` and `Infrastructure/` source from publish stage (already
   built in base)

Special cases:
- **McpServerWebSearch**: Build stages use base-sdk; runtime stage still uses its custom
  `playwright-base` image
- **McpServerCommandRunner**: Updated from `COPY . .` pattern to selective copy pattern
- **WebChat**: Still copies WebChat.Client alongside its own .csproj (unique to WebChat)

### Docker Compose changes

Add a `base-sdk` service that builds the shared image. Other services reference it via
`depends_on` for build ordering:

```yaml
base-sdk:
  image: base-sdk:latest
  build:
    context: ${REPOSITORY_PATH}
    dockerfile: Dockerfile.base-sdk
  entrypoint: ["true"]
  restart: "no"
  networks:
    - jackbot
```

Each built service (agent, webui, mcp-*) gets `base-sdk` added to its `depends_on` list.
This ensures `docker compose build` builds base-sdk first, then all leaf services in
parallel.

At runtime, base-sdk starts with `entrypoint: ["true"]`, immediately exits, and all
dependent services proceed normally.

## Performance Impact

**Saves:**
- Domain + Infrastructure compiled once instead of 8 times
- NuGet packages for Domain/Infrastructure restored once in base, available via BuildKit
  cache mount for all leaf builds
- Build parallelism preserved: after base-sdk completes, all 8 leaf services build in
  parallel with only their own small compilation

**Trade-off:**
- Introduces a serial dependency: base-sdk must finish before leaf builds start
- In practice, 8 concurrent .NET compilations thrash CPU/memory, so the net effect is
  faster

**Does not change:**
- Each leaf service still restores its own unique NuGet packages (fast with cache mount)
- Runtime base images (aspnet:10.0, playwright-base) still pulled per service (Docker
  layer dedup handles this)

## Files to create/modify

| File | Action |
|------|--------|
| `Dockerfile.base-sdk` | Create |
| `Agent/Dockerfile` | Modify |
| `WebChat/Dockerfile` | Modify |
| `McpServerText/Dockerfile` | Modify |
| `McpServerWebSearch/Dockerfile` | Modify |
| `McpServerLibrary/Dockerfile` | Modify |
| `McpServerMemory/Dockerfile` | Modify |
| `McpServerIdealista/Dockerfile` | Modify |
| `McpServerCommandRunner/Dockerfile` | Modify |
| `DockerCompose/docker-compose.yml` | Modify |
