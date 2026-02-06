#!/bin/bash
set -euo pipefail

echo "=== Devcontainer post-create setup ==="

# Install Claude Code globally
echo "Installing Claude Code..."
sudo npm install -g @anthropic-ai/claude-code

# Ensure NuGet cache directory exists with correct ownership
mkdir -p "$NUGET_PACKAGES"

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
    echo "WARNING: Docker daemon not available after 30s. DinD may not be running."
fi

echo "=== Setup complete ==="
echo "Run 'claude --dangerously-skip-permissions' to start Claude Code."
