#!/usr/bin/env bash
set -euo pipefail

PASS=0
FAIL=0

check() {
    local desc="$1" result="$2"
    if [ "$result" = "0" ]; then
        echo "  PASS: $desc"
        PASS=$((PASS + 1))
    else
        echo "  FAIL: $desc"
        FAIL=$((FAIL + 1))
    fi
}

check_inverse() {
    local desc="$1" result="$2"
    if [ "$result" != "0" ]; then
        echo "  PASS: $desc"
        PASS=$((PASS + 1))
    else
        echo "  FAIL: $desc"
        FAIL=$((FAIL + 1))
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
    grep -q 'FROM base-sdk:latest AS dependencies' "$df" && rc=0 || rc=$?
    check "Uses base-sdk:latest as dependencies base" "$rc"

    # Must NOT copy Domain.csproj in dependencies stage
    grep -q 'COPY.*Domain/Domain\.csproj' "$df" && rc=0 || rc=$?
    check_inverse "Does not copy Domain.csproj" "$rc"

    # Must NOT copy Infrastructure.csproj in dependencies stage
    grep -q 'COPY.*Infrastructure/Infrastructure\.csproj' "$df" && rc=0 || rc=$?
    check_inverse "Does not copy Infrastructure.csproj" "$rc"

    # Must NOT copy Domain/ source in publish stage
    grep -q 'COPY.*"Domain/"' "$df" && rc=0 || rc=$?
    check_inverse "Does not copy Domain/ source" "$rc"

    # Must NOT copy Infrastructure/ source in publish stage
    grep -q 'COPY.*"Infrastructure/"' "$df" && rc=0 || rc=$?
    check_inverse "Does not copy Infrastructure/ source" "$rc"

    # Must NOT have WORKDIR /src (inherited from base-sdk)
    grep -q 'WORKDIR /src' "$df" && rc=0 || rc=$?
    check_inverse "Does not set WORKDIR /src" "$rc"
done

echo ""
echo "=== Adversarial Checks: Preserved Properties ==="

# Derive the expected DLL name from the directory portion of each Dockerfile path
declare -A EXPECTED_DLLS=(
    ["Agent/Dockerfile"]="Agent.dll"
    ["McpServerText/Dockerfile"]="McpServerText.dll"
    ["McpServerLibrary/Dockerfile"]="McpServerLibrary.dll"
    ["McpServerMemory/Dockerfile"]="McpServerMemory.dll"
    ["McpServerIdealista/Dockerfile"]="McpServerIdealista.dll"
)

for df in "${STANDARD_DOCKERFILES[@]}"; do
    echo ""
    echo "Adversarial checks for $df..."

    # 1. syntax=docker/dockerfile:1 header must be present on the very first line
    first_line=$(head -1 "$df")
    [ "$first_line" = "# syntax=docker/dockerfile:1" ] && rc=0 || rc=1
    check "Has # syntax=docker/dockerfile:1 on first line" "$rc"

    # 2. Runtime base image must be aspnet:10.0
    grep -q 'FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base' "$df" && rc=0 || rc=$?
    check "Runtime base image is aspnet:10.0" "$rc"

    # 3. ENTRYPOINT must reference the correct DLL for this service
    expected_dll="${EXPECTED_DLLS[$df]}"
    grep -q "ENTRYPOINT \[\"dotnet\", \"${expected_dll}\"\]" "$df" && rc=0 || rc=$?
    check "ENTRYPOINT references correct DLL (${expected_dll})" "$rc"

    # 4. NuGet cache mount with sharing=locked must appear on BOTH restore and publish RUN commands
    cache_mount_count=$(grep -c 'mount=type=cache,target=/root/.nuget/packages,sharing=locked' "$df" || true)
    [ "$cache_mount_count" -eq 2 ] && rc=0 || rc=1
    check "NuGet cache mount (sharing=locked) present on both RUN commands (found ${cache_mount_count})" "$rc"

    # 5. Final stage must use FROM base AS final
    grep -q 'FROM base AS final' "$df" && rc=0 || rc=$?
    check "Final stage uses FROM base AS final" "$rc"

    # 6. Final stage must COPY from publish stage
    grep -q 'COPY --from=publish /app/publish' "$df" && rc=0 || rc=$?
    check "Final stage copies from publish stage" "$rc"
done

echo ""
echo "=== Special Dockerfile Structural Checks ==="

# --- McpServerWebSearch ---
echo ""
echo "Checking McpServerWebSearch/Dockerfile..."
df="McpServerWebSearch/Dockerfile"

grep -q 'FROM base-sdk:latest AS dependencies' "$df" && rc=0 || rc=$?
check "Uses base-sdk:latest as dependencies base" "$rc"

# Must still have playwright-base stage
grep -q 'playwright-base' "$df" && rc=0 || rc=$?
check "Retains playwright-base runtime stage" "$rc"

# Must NOT have sdk:10.0 in dependencies stage (may still have it in playwright-base)
# Check specifically that no FROM line references dotnet/sdk
grep -E '^FROM mcr.microsoft.com/dotnet/sdk' "$df" && rc=0 || rc=$?
check_inverse "No direct sdk:10.0 dependency stage" "$rc"

grep -q 'COPY.*"Domain/"' "$df" && rc=0 || rc=$?
check_inverse "Does not copy Domain/ source" "$rc"

grep -q 'COPY.*"Infrastructure/"' "$df" && rc=0 || rc=$?
check_inverse "Does not copy Infrastructure/ source" "$rc"

# --- McpServerCommandRunner ---
echo ""
echo "Checking McpServerCommandRunner/Dockerfile..."
df="McpServerCommandRunner/Dockerfile"

grep -q 'FROM base-sdk:latest AS dependencies' "$df" && rc=0 || rc=$?
check "Uses base-sdk:latest as dependencies base" "$rc"

# Must NOT use COPY . . (old pattern)
grep -q 'COPY \. \.' "$df" && rc=0 || rc=$?
check_inverse "Does not use COPY . ." "$rc"

grep -q 'COPY.*"Domain/"' "$df" && rc=0 || rc=$?
check_inverse "Does not copy Domain/ source" "$rc"

grep -q 'COPY.*"Infrastructure/"' "$df" && rc=0 || rc=$?
check_inverse "Does not copy Infrastructure/ source" "$rc"

# Should use cache mount for restore
grep -q 'mount=type=cache' "$df" && rc=0 || rc=$?
check "Uses NuGet cache mount" "$rc"

# --- WebChat ---
echo ""
echo "Checking WebChat/Dockerfile..."
df="WebChat/Dockerfile"

grep -q 'FROM base-sdk:latest AS dependencies' "$df" && rc=0 || rc=$?
check "Uses base-sdk:latest as dependencies base" "$rc"

# Must still copy WebChat.Client .csproj
grep -q 'COPY.*WebChat\.Client/WebChat\.Client\.csproj' "$df" && rc=0 || rc=$?
check "Still copies WebChat.Client.csproj" "$rc"

# Must still copy WebChat.Client/ source
grep -q 'COPY.*"WebChat\.Client/"' "$df" && rc=0 || rc=$?
check "Still copies WebChat.Client/ source" "$rc"

grep -q 'COPY.*"Domain/"' "$df" && rc=0 || rc=$?
check_inverse "Does not copy Domain/ source" "$rc"

grep -q 'COPY.*"Infrastructure/"' "$df" && rc=0 || rc=$?
check_inverse "Does not copy Infrastructure/ source" "$rc"

echo ""
echo "=== Adversarial Checks: Special Dockerfiles ==="

# --- McpServerWebSearch: playwright-base stage must be line-by-line identical to original ---
echo ""
echo "Adversarial checks for McpServerWebSearch/Dockerfile..."
df="McpServerWebSearch/Dockerfile"

# The playwright-base comment must be preserved
grep -q '# Install Chromium dependencies and Playwright browsers' "$df" && rc=0 || rc=$?
check "playwright-base stage retains Chromium install comment" "$rc"

# ENTRYPOINT must reference correct DLL
grep -q 'ENTRYPOINT \["dotnet", "McpServerWebSearch.dll"\]' "$df" && rc=0 || rc=$?
check "ENTRYPOINT references McpServerWebSearch.dll" "$rc"

# Final stage must use playwright-base (NOT base)
grep -q 'FROM playwright-base AS final' "$df" && rc=0 || rc=$?
check "Final stage uses playwright-base (not base)" "$rc"

# syntax header must be present
first_line=$(head -1 "$df")
[ "$first_line" = "# syntax=docker/dockerfile:1" ] && rc=0 || rc=1
check "Has # syntax=docker/dockerfile:1 on first line" "$rc"

# --- McpServerCommandRunner: adversarial checks ---
echo ""
echo "Adversarial checks for McpServerCommandRunner/Dockerfile..."
df="McpServerCommandRunner/Dockerfile"

# syntax header must be present (was missing before migration)
first_line=$(head -1 "$df")
[ "$first_line" = "# syntax=docker/dockerfile:1" ] && rc=0 || rc=1
check "Has # syntax=docker/dockerfile:1 on first line" "$rc"

# Must NOT have a separate 'build' stage (was build+publish, now just publish)
grep -q 'AS build' "$df" && rc=0 || rc=$?
check_inverse "Does not have a separate build stage" "$rc"

# ENTRYPOINT must reference correct DLL
grep -q 'ENTRYPOINT \["dotnet", "McpServerCommandRunner.dll"\]' "$df" && rc=0 || rc=$?
check "ENTRYPOINT references McpServerCommandRunner.dll" "$rc"

# NuGet cache mount (sharing=locked) must appear on BOTH restore and publish RUN commands
cache_mount_count=$(grep -c 'mount=type=cache,target=/root/.nuget/packages,sharing=locked' "$df" || true)
[ "$cache_mount_count" -eq 2 ] && rc=0 || rc=1
check "NuGet cache mount (sharing=locked) on both RUN commands (found ${cache_mount_count})" "$rc"

# --- WebChat: adversarial checks ---
echo ""
echo "Adversarial checks for WebChat/Dockerfile..."
df="WebChat/Dockerfile"

# ENV ASPNETCORE_ENVIRONMENT=Production must be present in final stage
grep -q 'ENV ASPNETCORE_ENVIRONMENT=Production' "$df" && rc=0 || rc=$?
check "ENV ASPNETCORE_ENVIRONMENT=Production is present" "$rc"

# ENTRYPOINT must reference correct DLL
grep -q 'ENTRYPOINT \["dotnet", "WebChat.dll"\]' "$df" && rc=0 || rc=$?
check "ENTRYPOINT references WebChat.dll" "$rc"

# syntax header must be present
first_line=$(head -1 "$df")
[ "$first_line" = "# syntax=docker/dockerfile:1" ] && rc=0 || rc=1
check "Has # syntax=docker/dockerfile:1 on first line" "$rc"

# Final stage must use FROM base AS final
grep -q 'FROM base AS final' "$df" && rc=0 || rc=$?
check "Final stage uses FROM base AS final" "$rc"

echo ""
echo "=== Final Adversarial Checks ==="

# --- Check: Dockerfile.base-sdk has NuGet cache mounts on BOTH RUN commands ---
echo ""
echo "Adversarial checks for Dockerfile.base-sdk..."
df="Dockerfile.base-sdk"

cache_mount_count=$(grep -c 'mount=type=cache,target=/root/.nuget/packages,sharing=locked' "$df" || true)
[ "$cache_mount_count" -eq 2 ] && rc=0 || rc=1
check "base-sdk has NuGet cache mount on both RUN commands (found ${cache_mount_count})" "$rc"

# base-sdk must set WORKDIR /src
grep -q 'WORKDIR /src' "$df" && rc=0 || rc=$?
check "base-sdk sets WORKDIR /src" "$rc"

# base-sdk must build with -c Release
grep -q '\-c Release' "$df" && rc=0 || rc=$?
check "base-sdk builds in Release configuration" "$rc"

# --- Check: No service Dockerfile references dotnet/sdk directly ---
echo ""
echo "Checking no service Dockerfile uses raw dotnet/sdk..."
ALL_SERVICE_DOCKERFILES=(
    "Agent/Dockerfile"
    "McpServerText/Dockerfile"
    "McpServerLibrary/Dockerfile"
    "McpServerMemory/Dockerfile"
    "McpServerIdealista/Dockerfile"
    "McpServerWebSearch/Dockerfile"
    "McpServerCommandRunner/Dockerfile"
    "WebChat/Dockerfile"
)
for df in "${ALL_SERVICE_DOCKERFILES[@]}"; do
    grep -q 'FROM mcr.microsoft.com/dotnet/sdk' "$df" && rc=0 || rc=$?
    check_inverse "$df does not reference dotnet/sdk directly" "$rc"
done

# --- Check: All 8 service Dockerfiles have both dependencies and publish stages ---
echo ""
echo "Checking all service Dockerfiles have consistent stage naming..."
for df in "${ALL_SERVICE_DOCKERFILES[@]}"; do
    grep -q 'AS dependencies' "$df" && rc=0 || rc=$?
    check "$df has dependencies stage" "$rc"

    grep -q 'AS publish' "$df" && rc=0 || rc=$?
    check "$df has publish stage" "$rc"
done

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] || exit 1
