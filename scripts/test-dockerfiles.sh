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
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] || exit 1
