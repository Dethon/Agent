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
    awk "found && /^  [a-z]/{exit} /^  ${svc}:/{found=1} found{print}" "$COMPOSE_FILE" | grep -q 'base-sdk' && rc=0 || rc=$?
    check "$svc depends_on includes base-sdk" "$rc"
done

echo ""
echo "=== Adversarial Checks ==="

# --- Check 1: docker compose config validates YAML is well-formed ---
DUMMY_ENV="REPOSITORY_PATH=. DATA_PATH=. VAULT_PATH=. PUID=1000 PGID=1000 CF_API_TOKEN=x ALLOWEDDDNSHOST=x SERVICEBUS__CONNECTIONSTRING=x WEBPUSH__PUBLICKEY=x WEBPUSH__PRIVATEKEY=x WEBPUSH__SUBJECT=x AGENTURL=x USERS__0__ID=x USERS__0__AVATARURL=x USERS__1__ID=x USERS__1__AVATARURL=x"
env $DUMMY_ENV docker compose -f "$COMPOSE_FILE" config > /dev/null 2>&1 && rc=0 || rc=$?
check "docker compose config succeeds (valid YAML)" "$rc"

# --- Check 2: Pre-existing depends_on relationships are preserved ---
# Use docker compose config (canonical YAML) to verify deps via python/grep on resolved output
RESOLVED=$(env $DUMMY_ENV docker compose -f "$COMPOSE_FILE" config 2>/dev/null)

# agent must still depend on: mcp-library, mcp-text, mcp-websearch, mcp-memory, mcp-idealista, redis
for dep in mcp-library mcp-text mcp-websearch mcp-memory mcp-idealista redis; do
    echo "$RESOLVED" | python3 -c "
import sys, yaml
config = yaml.safe_load(sys.stdin)
deps = config.get('services',{}).get('agent',{}).get('depends_on',{})
sys.exit(0 if '${dep}' in deps else 1)
" && rc=0 || rc=$?
    check "agent preserves depends_on: ${dep}" "$rc"
done

# mcp-library must still depend on: qbittorrent, jackett
for dep in qbittorrent jackett; do
    echo "$RESOLVED" | python3 -c "
import sys, yaml
config = yaml.safe_load(sys.stdin)
deps = config.get('services',{}).get('mcp-library',{}).get('depends_on',{})
sys.exit(0 if '${dep}' in deps else 1)
" && rc=0 || rc=$?
    check "mcp-library preserves depends_on: ${dep}" "$rc"
done

# mcp-memory must still depend on: redis
echo "$RESOLVED" | python3 -c "
import sys, yaml
config = yaml.safe_load(sys.stdin)
deps = config.get('services',{}).get('mcp-memory',{}).get('depends_on',{})
sys.exit(0 if 'redis' in deps else 1)
" && rc=0 || rc=$?
check "mcp-memory preserves depends_on: redis" "$rc"

# webui must still depend on: agent
echo "$RESOLVED" | python3 -c "
import sys, yaml
config = yaml.safe_load(sys.stdin)
deps = config.get('services',{}).get('webui',{}).get('depends_on',{})
sys.exit(0 if 'agent' in deps else 1)
" && rc=0 || rc=$?
check "webui preserves depends_on: agent" "$rc"

# caddy must still depend on: webui, agent
for dep in webui agent; do
    echo "$RESOLVED" | python3 -c "
import sys, yaml
config = yaml.safe_load(sys.stdin)
deps = config.get('services',{}).get('caddy',{}).get('depends_on',{})
sys.exit(0 if '${dep}' in deps else 1)
" && rc=0 || rc=$?
    check "caddy preserves depends_on: ${dep}" "$rc"
done

# --- Check 3: base-sdk has no unnecessary properties ---
BASE_SDK_BLOCK=$(awk '/^  base-sdk:/,/^  [a-z]/' "$COMPOSE_FILE")
for prop in "ports:" "volumes:" "env_file:" "container_name:" "environment:"; do
    echo "$BASE_SDK_BLOCK" | grep -q "$prop" && rc=1 || rc=0
    check "base-sdk does NOT have ${prop}" "$rc"
done

# --- Check 4: caddy does NOT depend on base-sdk (not a .NET project) ---
awk '/^  caddy:/,/^  [a-z]/' "$COMPOSE_FILE" | grep -q 'base-sdk' && rc=1 || rc=0
check "caddy does NOT depend on base-sdk" "$rc"

# --- Check 5: non-built services do NOT depend on base-sdk ---
for svc in redis qbittorrent filebrowser jackett plex; do
    awk "found && /^  [a-z]/{exit} /^  ${svc}:/{found=1} found{print}" "$COMPOSE_FILE" | grep -q 'base-sdk' && rc=1 || rc=0
    check "${svc} does NOT depend on base-sdk" "$rc"
done

echo ""
echo "=== Final Adversarial Checks ==="

# --- Check: base-sdk build context uses REPOSITORY_PATH variable ---
grep -A5 'base-sdk:' "$COMPOSE_FILE" | grep -q 'context: ${REPOSITORY_PATH}' && rc=0 || rc=$?
check "base-sdk build context uses REPOSITORY_PATH variable" "$rc"

# --- Check: base-sdk is on jackbot network ---
awk '/^  base-sdk:/{found=1; next} found && /^  [a-z]/{exit} found{print}' "$COMPOSE_FILE" | grep -q 'jackbot' && rc=0 || rc=$?
check "base-sdk is on jackbot network" "$rc"

# --- Check: Exactly 7 built services depend on base-sdk (not more, not fewer) ---
base_sdk_dep_count=$(grep -c '^\s*- base-sdk' "$COMPOSE_FILE" || true)
[ "$base_sdk_dep_count" -eq 7 ] && rc=0 || rc=1
check "Exactly 7 services depend on base-sdk (found ${base_sdk_dep_count})" "$rc"

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] || exit 1
