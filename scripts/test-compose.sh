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

COMPOSE_FILE="DockerCompose/docker-compose.yml"

echo "=== Docker Compose Structural Checks ==="

# base-sdk service exists
grep -q 'base-sdk:' "$COMPOSE_FILE" && rc=0 || rc=$?
check "base-sdk service exists" "$rc"

# base-sdk has correct Dockerfile
grep -A5 'base-sdk:' "$COMPOSE_FILE" | grep -q 'Dockerfile.base-sdk' && rc=0 || rc=$?
check "base-sdk uses Dockerfile.base-sdk" "$rc"

# base-sdk has entrypoint true
grep -A10 'base-sdk:' "$COMPOSE_FILE" | grep -q 'entrypoint:.*true' && rc=0 || rc=$?
check "base-sdk has entrypoint true" "$rc"

# base-sdk has restart no
grep -A10 'base-sdk:' "$COMPOSE_FILE" | grep -q 'restart:.*"no"' && rc=0 || rc=$?
check "base-sdk has restart no" "$rc"

# base-sdk has image name
grep -A3 'base-sdk:' "$COMPOSE_FILE" | grep -q 'image: base-sdk:latest' && rc=0 || rc=$?
check "base-sdk has image: base-sdk:latest" "$rc"

# Each built service depends on base-sdk
BUILT_SERVICES=("mcp-library" "mcp-text" "mcp-websearch" "mcp-memory" "mcp-idealista" "agent" "webui")
for svc in "${BUILT_SERVICES[@]}"; do
    # Find the service block's depends_on and check for base-sdk
    awk "/^  ${svc}:/,/^  [a-z]/" "$COMPOSE_FILE" | grep -q 'base-sdk' && rc=0 || rc=$?
    check "$svc depends_on includes base-sdk" "$rc"
done

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] || exit 1
