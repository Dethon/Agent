#!/bin/bash
set -euo pipefail

echo "=== Devcontainer manual setup ==="
echo "Claude Code and system tools are pre-installed in the image."
echo "This script is for optional post-start tasks."

# Restore NuGet packages if solution exists
if [ -f /workspace/*.sln ]; then
    echo "Restoring NuGet packages..."
    dotnet restore /workspace
fi

echo "=== Setup complete ==="
echo "Run 'claude --dangerously-skip-permissions' to start Claude Code."
