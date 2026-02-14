# Shared Base SDK Image — Implementation Plan

> **For Claude:** Execute this plan using subagents. Dispatch a fresh subagent per task
> using the Task tool (subagent_type: "general-purpose"). Each task is self-contained.
> NEVER skip test or review tasks. They are tracked separately and all must complete.

**Goal:** Create a shared `base-sdk` Docker image with pre-compiled Domain + Infrastructure
to eliminate redundant compilation across 8 service Dockerfiles.

**Architecture:** A new `Dockerfile.base-sdk` builds Domain + Infrastructure into a tagged
SDK image. Each service Dockerfile inherits from `base-sdk:latest` instead of
`dotnet/sdk:10.0`. Docker Compose orchestrates build ordering via a `base-sdk` service
with `depends_on`.

**Tech Stack:** Docker, Docker Compose, .NET 10 SDK, MSBuild incremental build

**Design Document:** `docs/plans/2026-02-14-shared-base-sdk-image-design.md`

---

## Task 0: Create Dockerfile.base-sdk

**Type:** Scaffolding
**Depends on:** None

This is the foundation all other tasks depend on. Create the base SDK image that
pre-compiles Domain + Infrastructure.

**Create:** `Dockerfile.base-sdk` (in repository root)

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

**Verification:**
```bash
# Build the base-sdk image from repository root
docker build -f Dockerfile.base-sdk -t base-sdk:latest .

# Verify image contains compiled output
docker run --rm base-sdk:latest ls /src/Infrastructure/bin/Release/net10.0/Infrastructure.dll
docker run --rm base-sdk:latest ls /src/Domain/bin/Release/net10.0/Domain.dll
```
Expected: Build succeeds, both DLLs exist.

**Commit:** `git commit -m "build: add Dockerfile.base-sdk with pre-compiled Domain and Infrastructure"`

---

## Feature 1: Standard Service Dockerfiles

These 5 services follow the identical pattern and can be migrated as a batch:
Agent, McpServerText, McpServerLibrary, McpServerMemory, McpServerIdealista.

### Task 1.1: Write structural validation tests for standard Dockerfiles (RED)

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 0 must be complete

**Design requirements being tested:**
- Each Dockerfile must use `FROM base-sdk:latest AS dependencies` (not `FROM dotnet/sdk:10.0`)
- Each Dockerfile must NOT contain `COPY ["Domain/Domain.csproj"` in the dependencies stage
- Each Dockerfile must NOT contain `COPY ["Infrastructure/Infrastructure.csproj"` in the dependencies stage
- Each Dockerfile must NOT contain `COPY ["Domain/"` in the publish stage
- Each Dockerfile must NOT contain `COPY ["Infrastructure/"` in the publish stage
- Each Dockerfile must NOT contain `WORKDIR /src` in the dependencies stage (inherited from base)

**Files:**
- Create: `scripts/test-dockerfiles.sh`

**What to test:**

```bash
#!/usr/bin/env bash
set -euo pipefail

PASS=0
FAIL=0

check() {
    local desc="$1" result="$2"
    if [ "$result" = "0" ]; then
        echo "  PASS: $desc"
        ((PASS++))
    else
        echo "  FAIL: $desc"
        ((FAIL++))
    fi
}

check_inverse() {
    local desc="$1" result="$2"
    if [ "$result" != "0" ]; then
        echo "  PASS: $desc"
        ((PASS++))
    else
        echo "  FAIL: $desc"
        ((FAIL++))
    fi
}

STANDARD_DOCKERFILES=(
    "Agent/Dockerfile"
    "McpServerText/Dockerfile"
    "McpServerLibrary/Dockerfile"
    "McpServerMemory/Dockerfile"
    "McpServerIdealista/Dockerfile"
)

echo "=== Standard Dockerfile Structural Checks ==="
for df in "${STANDARD_DOCKERFILES[@]}"; do
    echo ""
    echo "Checking $df..."

    # Must use base-sdk:latest
    grep -q 'FROM base-sdk:latest AS dependencies' "$df"
    check "Uses base-sdk:latest as dependencies base" "$?"

    # Must NOT copy Domain.csproj in dependencies stage
    grep -q 'COPY.*Domain/Domain\.csproj' "$df"
    check_inverse "Does not copy Domain.csproj" "$?"

    # Must NOT copy Infrastructure.csproj in dependencies stage
    grep -q 'COPY.*Infrastructure/Infrastructure\.csproj' "$df"
    check_inverse "Does not copy Infrastructure.csproj" "$?"

    # Must NOT copy Domain/ source in publish stage
    # We check for the source copy pattern: COPY ["Domain/", "Domain/"]
    grep -q 'COPY.*"Domain/"' "$df"
    check_inverse "Does not copy Domain/ source" "$?"

    # Must NOT copy Infrastructure/ source in publish stage
    grep -q 'COPY.*"Infrastructure/"' "$df"
    check_inverse "Does not copy Infrastructure/ source" "$?"

    # Must NOT have WORKDIR /src (inherited from base-sdk)
    grep -q 'WORKDIR /src' "$df"
    check_inverse "Does not set WORKDIR /src" "$?"
done

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] || exit 1
```

**Verification:**
```bash
chmod +x scripts/test-dockerfiles.sh
bash scripts/test-dockerfiles.sh
```
Expected: ALL checks FAIL (Dockerfiles still use the old pattern).

**Commit:** `git commit -m "test: add structural validation for standard Dockerfiles"`

---

### Task 1.2: Migrate standard service Dockerfiles to use base-sdk (GREEN)

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 1.1 must be complete (failing tests must exist)

**Goal:** Modify all 5 standard Dockerfiles to inherit from `base-sdk:latest` and remove
redundant Domain/Infrastructure copy steps. Minimal changes only.

**Files to modify:**
- `Agent/Dockerfile`
- `McpServerText/Dockerfile`
- `McpServerLibrary/Dockerfile`
- `McpServerMemory/Dockerfile`
- `McpServerIdealista/Dockerfile`

**Implementation pattern (apply to each):**

Replace the `dependencies` stage:
```dockerfile
# BEFORE:
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dependencies
WORKDIR /src
COPY ["<Project>/<Project>.csproj", "<Project>/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "<Project>/<Project>.csproj"

# AFTER:
FROM base-sdk:latest AS dependencies
COPY ["<Project>/<Project>.csproj", "<Project>/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "<Project>/<Project>.csproj"
```

Replace the `publish` stage:
```dockerfile
# BEFORE:
FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["Domain/", "Domain/"]
COPY ["Infrastructure/", "Infrastructure/"]
COPY ["<Project>/", "<Project>/"]
...

# AFTER:
FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["<Project>/", "<Project>/"]
...
```

The `base` stage (runtime aspnet:10.0) and `final` stage remain unchanged.

**Verification:**
```bash
bash scripts/test-dockerfiles.sh
```
Expected: ALL checks PASS.

**Commit:** `git commit -m "build: migrate standard Dockerfiles to use base-sdk image"`

---

### Task 1.3: Adversarial review of standard Dockerfiles

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 1.2 must be complete

**Your role:** You are an adversarial reviewer. Your job is to BREAK this implementation.

**Design requirements to verify:**
- Each of the 5 standard Dockerfiles uses `FROM base-sdk:latest AS dependencies`
- No Dockerfile duplicates Domain/Infrastructure copy or restore steps
- Runtime base image (`aspnet:10.0`) is unchanged
- Final stage and ENTRYPOINT are unchanged
- NuGet cache mount is preserved on restore and publish RUN commands
- `# syntax=docker/dockerfile:1` header is preserved where it existed

**Review checklist:**

1. **Design compliance** — Read each modified Dockerfile. Compare against the design
   document pattern. Is anything missing? Are any Domain/Infrastructure references
   accidentally left in?

2. **Test adequacy** — Does the test script catch all the patterns? Could a broken
   Dockerfile pass the tests? Add checks for:
   - Runtime base stage is still `aspnet:10.0`
   - ENTRYPOINT matches the correct DLL name
   - `# syntax=docker/dockerfile:1` header present

3. **Edge cases** — Check that the Agent Dockerfile doesn't have any unique patterns
   that were accidentally removed. Check that NuGet cache mounts use the correct
   `sharing=locked` option.

4. **Build verification** — Build base-sdk, then build at least 2 of the standard
   services to verify they compile successfully:
   ```bash
   docker build -f Dockerfile.base-sdk -t base-sdk:latest .
   docker build -f Agent/Dockerfile -t agent:test .
   docker build -f McpServerText/Dockerfile -t mcp-text:test .
   ```

**You MUST write and run at least 3 additional test checks** targeting gaps in the
existing test script. Add them to `scripts/test-dockerfiles.sh`.

**What to produce:**
- List of issues found (Critical / Important / Minor)
- Additional tests written and their results
- Verdict: PASS or FAIL

**If FAIL:** Create fix tasks following the same pattern.

**Commit additional tests:** `git commit -m "test: add adversarial checks for standard Dockerfiles"`

---

## Feature 2: Special Service Dockerfiles

Three services have unique patterns: McpServerWebSearch (Playwright runtime base),
McpServerCommandRunner (different build pattern), WebChat (extra WebChat.Client project).

### Task 2.1: Write structural validation tests for special Dockerfiles (RED)

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 1.3 must be complete (standard Dockerfiles reviewed)

**Design requirements being tested:**

**McpServerWebSearch:**
- Build stages use `FROM base-sdk:latest AS dependencies` (not sdk:10.0)
- Runtime stage still uses custom `playwright-base` (NOT aspnet:10.0)
- Playwright browser installation in runtime stage is unchanged
- No Domain/Infrastructure copy in dependencies or publish stages

**McpServerCommandRunner:**
- Uses `FROM base-sdk:latest AS dependencies` (not sdk:10.0)
- No longer uses `COPY . .` (selective copy only)
- No Domain/Infrastructure copy in build/publish stages
- Uses NuGet cache mount (currently missing, should be added)

**WebChat:**
- Uses `FROM base-sdk:latest AS dependencies`
- Still copies `WebChat.Client/WebChat.Client.csproj` alongside `WebChat/WebChat.csproj`
- Still copies `WebChat.Client/` source in publish stage
- No Domain/Infrastructure copy

**Files:**
- Modify: `scripts/test-dockerfiles.sh` (append special checks)

**What to test:**

```bash
# Append to scripts/test-dockerfiles.sh:

echo ""
echo "=== Special Dockerfile Structural Checks ==="

# --- McpServerWebSearch ---
echo ""
echo "Checking McpServerWebSearch/Dockerfile..."
df="McpServerWebSearch/Dockerfile"

grep -q 'FROM base-sdk:latest AS dependencies' "$df"
check "Uses base-sdk:latest as dependencies base" "$?"

# Must still have playwright-base stage
grep -q 'playwright-base' "$df"
check "Retains playwright-base runtime stage" "$?"

# Must NOT have sdk:10.0 in dependencies stage (may still have it in playwright-base)
# Check specifically that the dependencies FROM is base-sdk, not sdk
grep -E '^FROM mcr.microsoft.com/dotnet/sdk' "$df"
check_inverse "No direct sdk:10.0 dependency stage" "$?"

grep -q 'COPY.*"Domain/"' "$df"
check_inverse "Does not copy Domain/ source" "$?"

grep -q 'COPY.*"Infrastructure/"' "$df"
check_inverse "Does not copy Infrastructure/ source" "$?"

# --- McpServerCommandRunner ---
echo ""
echo "Checking McpServerCommandRunner/Dockerfile..."
df="McpServerCommandRunner/Dockerfile"

grep -q 'FROM base-sdk:latest AS dependencies' "$df"
check "Uses base-sdk:latest as dependencies base" "$?"

# Must NOT use COPY . . (old pattern)
grep -q 'COPY \. \.' "$df"
check_inverse "Does not use COPY . ." "$?"

grep -q 'COPY.*"Domain/"' "$df"
check_inverse "Does not copy Domain/ source" "$?"

grep -q 'COPY.*"Infrastructure/"' "$df"
check_inverse "Does not copy Infrastructure/ source" "$?"

# Should use cache mount for restore
grep -q 'mount=type=cache' "$df"
check "Uses NuGet cache mount" "$?"

# --- WebChat ---
echo ""
echo "Checking WebChat/Dockerfile..."
df="WebChat/Dockerfile"

grep -q 'FROM base-sdk:latest AS dependencies' "$df"
check "Uses base-sdk:latest as dependencies base" "$?"

# Must still copy WebChat.Client .csproj
grep -q 'COPY.*WebChat\.Client/WebChat\.Client\.csproj' "$df"
check "Still copies WebChat.Client.csproj" "$?"

# Must still copy WebChat.Client/ source
grep -q 'COPY.*"WebChat\.Client/"' "$df"
check "Still copies WebChat.Client/ source" "$?"

grep -q 'COPY.*"Domain/"' "$df"
check_inverse "Does not copy Domain/ source" "$?"

grep -q 'COPY.*"Infrastructure/"' "$df"
check_inverse "Does not copy Infrastructure/ source" "$?"
```

**Verification:**
```bash
bash scripts/test-dockerfiles.sh
```
Expected: Standard checks PASS (from Task 1.2), special checks FAIL (not yet modified).

**Commit:** `git commit -m "test: add structural validation for special Dockerfiles"`

---

### Task 2.2: Migrate special service Dockerfiles to use base-sdk (GREEN)

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 2.1 must be complete

**Goal:** Modify the 3 special Dockerfiles to use base-sdk. Each has unique considerations.

**Files to modify:**
- `McpServerWebSearch/Dockerfile`
- `McpServerCommandRunner/Dockerfile`
- `WebChat/Dockerfile`

**McpServerWebSearch/Dockerfile — implementation:**

The `playwright-base` runtime stage is unchanged. Only the build stages change:

```dockerfile
# syntax=docker/dockerfile:1

# Stage 1: Runtime base with Playwright browsers (UNCHANGED)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS playwright-base
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN apt-get update && apt-get install -y --no-install-recommends \
    nodejs npm \
    libnss3 libnspr4 libatk1.0-0t64 libatk-bridge2.0-0t64 libcups2t64 \
    libdrm2 libdbus-1-3 libxkbcommon0 libatspi2.0-0t64 libxcomposite1 \
    libxdamage1 libxfixes3 libxrandr2 libgbm1 libasound2t64 \
    libpango-1.0-0 libcairo2 libxshmfence1 \
    && npx playwright install chromium \
    && chmod -R 755 /ms-playwright \
    && apt-get purge -y nodejs npm \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/* /root/.npm /tmp/*

# Stage 2: Restore dependencies (CHANGED: uses base-sdk)
FROM base-sdk:latest AS dependencies
COPY ["McpServerWebSearch/McpServerWebSearch.csproj", "McpServerWebSearch/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "McpServerWebSearch/McpServerWebSearch.csproj"

# Stage 3: Publish (CHANGED: no Domain/Infrastructure copy)
FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerWebSearch/", "McpServerWebSearch/"]
WORKDIR "/src/McpServerWebSearch"
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "./McpServerWebSearch.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Stage 4: Final runtime image (UNCHANGED)
FROM playwright-base AS final
WORKDIR /app
COPY --from=publish /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "McpServerWebSearch.dll"]
```

**McpServerCommandRunner/Dockerfile — implementation:**

This Dockerfile currently uses `COPY . .` and a separate build+publish pattern. Modernize
it to match the standard pattern while using base-sdk:

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpServerCommandRunner/McpServerCommandRunner.csproj", "McpServerCommandRunner/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "McpServerCommandRunner/McpServerCommandRunner.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerCommandRunner/", "McpServerCommandRunner/"]
WORKDIR "/src/McpServerCommandRunner"
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "./McpServerCommandRunner.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServerCommandRunner.dll"]
```

**WebChat/Dockerfile — implementation:**

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["WebChat/WebChat.csproj", "WebChat/"]
COPY ["WebChat.Client/WebChat.Client.csproj", "WebChat.Client/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "WebChat/WebChat.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["WebChat/", "WebChat/"]
COPY ["WebChat.Client/", "WebChat.Client/"]
WORKDIR "/src/WebChat"
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "./WebChat.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "WebChat.dll"]
```

**Verification:**
```bash
bash scripts/test-dockerfiles.sh
```
Expected: ALL checks PASS (both standard and special).

**Commit:** `git commit -m "build: migrate special Dockerfiles to use base-sdk image"`

---

### Task 2.3: Adversarial review of special Dockerfiles

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 2.2 must be complete

**Your role:** You are an adversarial reviewer. Your job is to BREAK this implementation.

**Design requirements to verify:**
- McpServerWebSearch: playwright-base runtime stage is completely unchanged (every line)
- McpServerWebSearch: build stages use base-sdk, no Domain/Infrastructure copies
- McpServerCommandRunner: modernized from `COPY . .` to selective copy pattern
- McpServerCommandRunner: now uses NuGet cache mounts (was missing before)
- McpServerCommandRunner: `# syntax=docker/dockerfile:1` header added
- WebChat: still copies WebChat.Client .csproj and source
- WebChat: ENV ASPNETCORE_ENVIRONMENT=Production preserved in final stage
- All 3: no Domain/Infrastructure copies in any stage

**Review checklist:**

1. **Design compliance** — Read each modified Dockerfile line by line. Compare the
   playwright-base stage against the original (must be identical). Verify WebChat.Client
   handling is preserved.

2. **Test adequacy** — Could broken Dockerfiles pass the structural tests? Add checks for:
   - McpServerWebSearch ENTRYPOINT is correct
   - McpServerCommandRunner no longer has a separate `build` stage (was build+publish,
     now just publish)
   - WebChat ENV ASPNETCORE_ENVIRONMENT=Production is present

3. **Edge cases** — Verify McpServerCommandRunner's `COPY . .` is truly gone and no
   extraneous files could leak into the image. Verify that removing the separate build
   stage doesn't break the publish (publish includes build implicitly).

4. **Build verification** — Build base-sdk, then build the 3 special services:
   ```bash
   docker build -f Dockerfile.base-sdk -t base-sdk:latest .
   docker build -f McpServerWebSearch/Dockerfile -t mcp-websearch:test .
   docker build -f McpServerCommandRunner/Dockerfile -t mcp-commandrunner:test .
   docker build -f WebChat/Dockerfile -t webui:test .
   ```

**You MUST write and run at least 3 additional test checks.** Add them to
`scripts/test-dockerfiles.sh`.

**What to produce:**
- List of issues found (Critical / Important / Minor)
- Additional tests written and their results
- Verdict: PASS or FAIL

**If FAIL:** Create fix tasks.

**Commit additional tests:** `git commit -m "test: add adversarial checks for special Dockerfiles"`

---

## Feature 3: Docker Compose Integration

### Task 3.1: Write validation tests for compose changes (RED)

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 2.3 must be complete

**Design requirements being tested:**
- A `base-sdk` service exists in docker-compose.yml
- `base-sdk` service has `build.dockerfile: Dockerfile.base-sdk`
- `base-sdk` service has `entrypoint: ["true"]` and `restart: "no"`
- `base-sdk` service has `image: base-sdk:latest`
- All built services (agent, webui, mcp-*) include `base-sdk` in their `depends_on`

**Files:**
- Create: `scripts/test-compose.sh`

**What to test:**

```bash
#!/usr/bin/env bash
set -euo pipefail

PASS=0
FAIL=0

check() {
    local desc="$1" result="$2"
    if [ "$result" = "0" ]; then
        echo "  PASS: $desc"
        ((PASS++))
    else
        echo "  FAIL: $desc"
        ((FAIL++))
    fi
}

COMPOSE_FILE="DockerCompose/docker-compose.yml"

echo "=== Docker Compose Structural Checks ==="

# base-sdk service exists
grep -q 'base-sdk:' "$COMPOSE_FILE"
check "base-sdk service exists" "$?"

# base-sdk has correct Dockerfile
grep -A5 'base-sdk:' "$COMPOSE_FILE" | grep -q 'Dockerfile.base-sdk'
check "base-sdk uses Dockerfile.base-sdk" "$?"

# base-sdk has entrypoint true
grep -A10 'base-sdk:' "$COMPOSE_FILE" | grep -q 'entrypoint:.*true'
check "base-sdk has entrypoint true" "$?"

# base-sdk has restart no
grep -A10 'base-sdk:' "$COMPOSE_FILE" | grep -q 'restart:.*"no"'
check "base-sdk has restart no" "$?"

# base-sdk has image name
grep -A3 'base-sdk:' "$COMPOSE_FILE" | grep -q 'image: base-sdk:latest'
check "base-sdk has image: base-sdk:latest" "$?"

# Each built service depends on base-sdk
BUILT_SERVICES=("mcp-library" "mcp-text" "mcp-websearch" "mcp-memory" "mcp-idealista" "agent" "webui")
for svc in "${BUILT_SERVICES[@]}"; do
    # Find the service block's depends_on and check for base-sdk
    # Use python/yq for reliable YAML parsing, or approximate with grep
    awk "/^  ${svc}:/,/^  [a-z]/" "$COMPOSE_FILE" | grep -q 'base-sdk'
    check "$svc depends_on includes base-sdk" "$?"
done

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] || exit 1
```

**Verification:**
```bash
chmod +x scripts/test-compose.sh
bash scripts/test-compose.sh
```
Expected: ALL checks FAIL (compose file not yet modified).

**Commit:** `git commit -m "test: add structural validation for compose base-sdk integration"`

---

### Task 3.2: Add base-sdk service to docker-compose.yml (GREEN)

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 3.1 must be complete

**Goal:** Add the `base-sdk` service and update `depends_on` for all built services.

**File to modify:** `DockerCompose/docker-compose.yml`

**Changes:**

1. Add `base-sdk` service before `mcp-library` (first built service):

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

2. Add `base-sdk` to `depends_on` for each built service. For services that already have
   `depends_on`, add `base-sdk` to the list. For services without `depends_on`, add it.

   Services to update:
   - `mcp-library`: add `- base-sdk` to existing depends_on
   - `mcp-text`: add depends_on with `- base-sdk`
   - `mcp-websearch`: add depends_on with `- base-sdk`
   - `mcp-memory`: add `- base-sdk` to existing depends_on
   - `mcp-idealista`: add depends_on with `- base-sdk`
   - `agent`: add `- base-sdk` to existing depends_on
   - `webui`: add `- base-sdk` to existing depends_on

**Verification:**
```bash
bash scripts/test-compose.sh
```
Expected: ALL checks PASS.

**Commit:** `git commit -m "build: add base-sdk service to docker-compose and wire depends_on"`

---

### Task 3.3: Adversarial review of compose integration

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 3.2 must be complete

**Your role:** You are an adversarial reviewer. Your job is to BREAK this integration.

**Design requirements to verify:**
- base-sdk service has all required properties (image, build, entrypoint, restart)
- All 7 built services depend on base-sdk for build ordering
- Existing depends_on relationships are preserved (not broken by adding base-sdk)
- No other services were accidentally modified
- YAML indentation is consistent with the rest of the file

**Review checklist:**

1. **Design compliance** — Read the full compose file. Verify base-sdk service structure.
   Verify every built service includes base-sdk in depends_on.

2. **Test adequacy** — Could a broken compose file pass the tests? The awk-based parsing
   is approximate. Add checks for:
   - `docker compose -f DockerCompose/docker-compose.yml config` succeeds (valid YAML)
   - Existing depends_on entries for agent (mcp-library, mcp-text, etc.) are preserved
   - base-sdk service doesn't have unnecessary properties (ports, volumes, env_file)

3. **Edge cases** — What happens if base-sdk fails to build? Does compose bail out or
   continue? What if base-sdk image is stale from a previous build? Consider whether
   `cache_from` should be added to base-sdk.

4. **Integration** — Run `docker compose -f DockerCompose/docker-compose.yml config`
   with required env vars to verify the full config is valid.

**You MUST write and run at least 3 additional test checks.** Add them to
`scripts/test-compose.sh`.

**What to produce:**
- List of issues found (Critical / Important / Minor)
- Additional tests written and their results
- Verdict: PASS or FAIL

**If FAIL:** Create fix tasks.

**Commit additional tests:** `git commit -m "test: add adversarial checks for compose integration"`

---

## Feature 4: End-to-End Integration

### Task 4.1: Write end-to-end build verification test (RED)

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 3.3 must be complete

**Design requirements being tested:**
- `docker compose build` builds base-sdk first, then all services successfully
- Built images contain the correct entrypoint DLLs
- base-sdk image is reused (not rebuilt) for each leaf service

**Files:**
- Create: `scripts/test-e2e-build.sh`

**What to test:**

```bash
#!/usr/bin/env bash
set -euo pipefail

PASS=0
FAIL=0

check() {
    local desc="$1" result="$2"
    if [ "$result" = "0" ]; then
        echo "  PASS: $desc"
        ((PASS++))
    else
        echo "  FAIL: $desc"
        ((FAIL++))
    fi
}

echo "=== End-to-End Build Verification ==="

# Step 1: Build base-sdk
echo ""
echo "Building base-sdk..."
docker build -f Dockerfile.base-sdk -t base-sdk:latest . > /dev/null 2>&1
check "base-sdk builds successfully" "$?"

# Step 2: Verify base-sdk contains compiled assemblies
docker run --rm base-sdk:latest test -f /src/Infrastructure/bin/Release/net10.0/Infrastructure.dll
check "base-sdk contains Infrastructure.dll" "$?"

docker run --rm base-sdk:latest test -f /src/Domain/bin/Release/net10.0/Domain.dll
check "base-sdk contains Domain.dll" "$?"

# Step 3: Build each leaf service
SERVICES=(
    "Agent/Dockerfile:agent:Agent.dll"
    "McpServerText/Dockerfile:mcp-text:McpServerText.dll"
    "McpServerLibrary/Dockerfile:mcp-library:McpServerLibrary.dll"
    "McpServerMemory/Dockerfile:mcp-memory:McpServerMemory.dll"
    "McpServerIdealista/Dockerfile:mcp-idealista:McpServerIdealista.dll"
    "McpServerWebSearch/Dockerfile:mcp-websearch:McpServerWebSearch.dll"
    "McpServerCommandRunner/Dockerfile:mcp-commandrunner:McpServerCommandRunner.dll"
    "WebChat/Dockerfile:webui:WebChat.dll"
)

for entry in "${SERVICES[@]}"; do
    IFS=':' read -r dockerfile tag dll <<< "$entry"
    echo ""
    echo "Building $tag from $dockerfile..."
    docker build -f "$dockerfile" -t "$tag:test" . > /dev/null 2>&1
    check "$tag builds successfully" "$?"

    # Verify the entrypoint DLL exists in the final image
    docker run --rm --entrypoint="" "$tag:test" test -f "/app/$dll"
    check "$tag contains $dll" "$?"
done

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] || exit 1
```

**Verification:**
```bash
chmod +x scripts/test-e2e-build.sh
bash scripts/test-e2e-build.sh
```
Expected: base-sdk builds (Task 0 created it), but leaf services FAIL if Dockerfile
changes are not yet applied. If all previous tasks are complete, this should PASS
(serving as a comprehensive integration check).

**Commit:** `git commit -m "test: add end-to-end Docker build verification"`

---

### Task 4.2: Fix any integration failures (GREEN)

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 4.1 must be complete

**Goal:** Run the e2e build test and fix any failures. If all previous tasks were
implemented correctly, this should be a no-op (all builds pass).

**Verification:**
```bash
bash scripts/test-e2e-build.sh
```
Expected: ALL checks PASS.

**If any failures:** Fix the root cause in the relevant Dockerfile or compose file.
Commit fixes with descriptive messages.

**Commit (if changes needed):** `git commit -m "fix: resolve integration build failures for base-sdk migration"`

---

### Task 4.3: Final adversarial review

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 4.2 must be complete (all builds passing)

**Your role:** Final adversarial review of the entire implementation against ALL design
requirements.

**Design requirements checklist (verify each one):**
- [ ] `Dockerfile.base-sdk` exists and builds Domain + Infrastructure
- [ ] All 8 service Dockerfiles use `FROM base-sdk:latest AS dependencies`
- [ ] No service Dockerfile copies Domain.csproj or Infrastructure.csproj
- [ ] No service Dockerfile copies Domain/ or Infrastructure/ source
- [ ] McpServerWebSearch retains playwright-base runtime stage unchanged
- [ ] McpServerCommandRunner modernized from `COPY . .` to selective copy
- [ ] WebChat still handles WebChat.Client project
- [ ] docker-compose.yml has base-sdk service with correct configuration
- [ ] All 7 built services depend on base-sdk
- [ ] Existing runtime depends_on relationships preserved
- [ ] NuGet cache mounts preserved on all RUN commands that need them
- [ ] All Docker images build successfully

**Review approach:**

1. Read every modified file and check against the checklist above.

2. Verify the full build works:
   ```bash
   bash scripts/test-dockerfiles.sh
   bash scripts/test-compose.sh
   bash scripts/test-e2e-build.sh
   ```

3. Try to break it:
   - What happens if someone modifies Domain/ but forgets to rebuild base-sdk? (Answer:
     leaf builds would use stale Domain — this is expected behavior, documented trade-off)
   - What happens if base-sdk:latest doesn't exist when building a leaf service? (Answer:
     Docker build fails with clear error — acceptable)
   - Are there any secrets or sensitive files that could leak into the base-sdk image?

4. Write at least 3 additional tests across any of the test scripts.

**What to produce:**
- Checklist with pass/fail for each design requirement
- Issues found (Critical / Important / Minor)
- Additional tests and their results
- Final verdict: PASS or FAIL

**If FAIL:** Create fix tasks.

**Commit additional tests:** `git commit -m "test: add final adversarial checks for base-sdk migration"`

---

## Dependency Graph

```
Task 0 (Scaffolding: Dockerfile.base-sdk)
  │
  ├─→ Task 1.1 (RED: standard Dockerfile tests)
  │     └─→ Task 1.2 (GREEN: migrate 5 standard Dockerfiles)
  │           └─→ Task 1.3 (REVIEW: adversarial review)
  │                 │
  │                 ├─→ Task 2.1 (RED: special Dockerfile tests)
  │                 │     └─→ Task 2.2 (GREEN: migrate 3 special Dockerfiles)
  │                 │           └─→ Task 2.3 (REVIEW: adversarial review)
  │                 │                 │
  │                 │                 └─→ Task 3.1 (RED: compose tests)
  │                 │                       └─→ Task 3.2 (GREEN: compose changes)
  │                 │                             └─→ Task 3.3 (REVIEW: adversarial review)
  │                 │                                   │
  │                 │                                   └─→ Task 4.1 (RED: e2e build test)
  │                 │                                         └─→ Task 4.2 (GREEN: fix issues)
  │                 │                                               └─→ Task 4.3 (REVIEW: final)
```

All tasks are strictly sequential — each depends on the previous completing successfully.
No parallel execution possible because each feature builds on the previous one's changes.

---

## Execution Instructions

**Recommended:** Execute using subagents for fresh context per task.

For each task, dispatch a fresh subagent using the Task tool:
- subagent_type: "general-purpose"
- Provide the FULL task text in the prompt (don't make subagent read this file)
- Include relevant context from earlier tasks (what was built, where files are)

**Execution order:**
- Tasks within a triplet are strictly sequential: N.1 → N.2 → N.3
- All triplets are sequential (each builds on previous changes)
- Task 0 must complete before any triplet begins

**Never:**
- Skip a test-writing task (N.1) — "I'll write tests with the implementation"
- Skip an adversarial review task (N.3) — "The tests already pass, it's fine"
- Combine tasks within a triplet — each is a separate subagent dispatch
- Proceed to N.2 if N.1 tests don't compile/exist
- Proceed to N.3 if N.2 tests don't pass
- Proceed to next triplet if N.3 verdict is FAIL
