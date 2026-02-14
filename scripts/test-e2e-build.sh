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

echo "=== End-to-End Build Verification ==="

# Step 1: Build base-sdk
echo ""
echo "Building base-sdk..."
docker build -f Dockerfile.base-sdk -t base-sdk:latest . > /dev/null 2>&1 && rc=0 || rc=$?
check "base-sdk builds successfully" "$rc"

# Step 2: Verify base-sdk contains compiled assemblies
docker run --rm base-sdk:latest test -f /src/Infrastructure/bin/Release/net10.0/Infrastructure.dll && rc=0 || rc=$?
check "base-sdk contains Infrastructure.dll" "$rc"

docker run --rm base-sdk:latest test -f /src/Domain/bin/Release/net10.0/Domain.dll && rc=0 || rc=$?
check "base-sdk contains Domain.dll" "$rc"

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
    docker build -f "$dockerfile" -t "$tag:test" . > /dev/null 2>&1 && rc=0 || rc=$?
    check "$tag builds successfully" "$rc"

    # Verify the entrypoint DLL exists in the final image
    docker run --rm --entrypoint="" "$tag:test" test -f "/app/$dll" && rc=0 || rc=$?
    check "$tag contains $dll" "$rc"
done

# Step 4: Verify base-sdk image contains ONLY Domain and Infrastructure under /src
echo ""
echo "Verifying base-sdk image contents..."
src_dirs=$(docker run --rm base-sdk:latest ls /src/)
expected_dirs=$'Domain\nInfrastructure'
[ "$src_dirs" = "$expected_dirs" ] && rc=0 || rc=1
check "base-sdk /src contains only Domain and Infrastructure" "$rc"

# Step 5: Verify no sensitive files leaked into base-sdk image
sensitive_count=$(docker run --rm base-sdk:latest find /src -name "*.env" -o -name "*.secret*" -o -name "*.key" -o -name "*.pfx" -o -name "*.pem" -o -name "appsettings.Development.json" -o -name "launchSettings.json" -o -name "*.user" 2>/dev/null | wc -l)
[ "$sensitive_count" -eq 0 ] && rc=0 || rc=1
check "base-sdk contains no sensitive files (found ${sensitive_count})" "$rc"

# Step 6: Verify base-sdk WORKDIR is /src
workdir=$(docker run --rm base-sdk:latest pwd)
[ "$workdir" = "/src" ] && rc=0 || rc=1
check "base-sdk WORKDIR is /src" "$rc"

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] || exit 1
