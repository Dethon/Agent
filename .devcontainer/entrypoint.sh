#!/bin/bash
set -euo pipefail

# Configure git safe directory (mounted workspace)
git config --global --add safe.directory /workspace

# Wait for DinD to be ready
echo "Waiting for Docker daemon (DinD)..."
timeout=30
until docker info >/dev/null 2>&1 || [ $timeout -le 0 ]; do
    sleep 1
    timeout=$((timeout - 1))
done

if docker info >/dev/null 2>&1; then
    echo "Docker daemon is ready."
else
    echo "WARNING: Docker daemon not available after 30s."
fi

# Execute the container command (default: sleep infinity from docker-compose)
exec "$@"
