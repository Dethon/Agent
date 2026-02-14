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
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] || exit 1
