#!/usr/bin/env bash
set -euo pipefail
# Static aarch64-musl release build of nabu-satellite.
# Wraps cargo-zigbuild with the fp16 CC shim (see zigcc-fp16-shim.sh for why).
cd "$(dirname "$0")/.."
command -v cargo-zigbuild >/dev/null 2>&1 || {
    echo "error: cargo-zigbuild not on PATH (install: python3 -m pip install --user cargo-zigbuild; ensure ~/.local/bin is on PATH)" >&2
    exit 1
}
export CC_aarch64_unknown_linux_musl="$(pwd)/scripts/zigcc-fp16-shim.sh"
cargo zigbuild --target aarch64-unknown-linux-musl --release
ls -lh target/aarch64-unknown-linux-musl/release/nabu-satellite
file target/aarch64-unknown-linux-musl/release/nabu-satellite
