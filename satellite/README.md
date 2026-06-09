# nabu-satellite

A single static Rust binary that acts as a drop-in [Wyoming protocol](https://github.com/rhasspy/wyoming)
satellite **server**, replacing the EOL Python [wyoming-satellite](https://github.com/rhasspy/wyoming-satellite)
on Raspberry Pi voice satellites. Wake-word detection ("ok nabu", openWakeWord ONNX models) runs embedded
in-process via [tract](https://github.com/sonos/tract) — no Python, no ONNX Runtime, no shared-library
dependencies on the target device.

## Build

### Host (dev)

```sh
cargo build
cargo test --release   # the wake-pipeline spike runs real tract inference; release keeps it fast
```

### Cross build (Raspberry Pi, fully static aarch64-musl)

Verified working toolchain:

| Component | Version |
|---|---|
| rustc | 1.91.1 (`rust-toolchain.toml` pins 1.91 + the `aarch64-unknown-linux-musl` target) |
| cargo-zigbuild | 0.22.3 (`pip install cargo-zigbuild ziglang`) |
| zig | 0.16.0, provided by the `ziglang` pip package (`python3 -m ziglang version`) |

**Caveat — tract-linalg vs zig cc:** `tract-linalg` 0.23 compiles its SVE f16 C kernels with
`-march=armv8.2-a+sve+fp16`. zig cc rejects GCC's `fp16` extension name
(`error: unknown CPU feature: 'fp16'`) because LLVM/zig spell it `fullfp16`. A plain
`cargo zigbuild --target aarch64-unknown-linux-musl --release` therefore fails in
`tract-linalg`'s build script. Work around it with a tiny CC shim that rewrites the feature
name and delegates to the same `zig cc` (this is what cargo-zigbuild's generated wrapper runs):

```sh
cat > /tmp/zigcc-fp16-shim.sh <<'EOF'
#!/bin/sh
# zig cc rejects GCC's `+fp16` -march extension name; LLVM/zig spell it `fullfp16`.
# tract-linalg hardcodes `-march=armv8.2-a+sve+fp16` for its SVE f16 kernels, so
# rewrite the feature name and delegate to `zig cc` exactly like cargo-zigbuild's
# generated wrapper does.
export CARGO_ZIGBUILD_ZIG_VERSION=0.16.0
n=$#
i=0
while [ "$i" -lt "$n" ]; do
    arg=$1
    shift
    case "$arg" in
        -march=*) arg=$(printf '%s' "$arg" | sed -e 's/+fp16$/+fullfp16/' -e 's/+fp16+/+fullfp16+/g') ;;
    esac
    set -- "$@" "$arg"
    i=$((i + 1))
done
exec cargo-zigbuild zig cc -- -g -fno-sanitize=all -target aarch64-linux-musl "$@"
EOF
chmod +x /tmp/zigcc-fp16-shim.sh
```

Then build (the env var makes cc-rs use the shim for C code; Rust code and linking still go
through cargo-zigbuild's own wrappers):

```sh
CC_aarch64_unknown_linux_musl=/tmp/zigcc-fp16-shim.sh \
    cargo zigbuild --target aarch64-unknown-linux-musl --release
```

Output: `target/aarch64-unknown-linux-musl/release/nabu-satellite` —
`ELF 64-bit LSB executable, ARM aarch64 ... statically linked, stripped`,
**1,248,184 bytes (1.2 MiB)** for the current skeleton (fat LTO, `strip = true`).
Execution verified on arm64 via Docker binfmt emulation (no Pi needed):

```sh
docker run --rm --platform linux/arm64 \
    -v "$PWD/target/aarch64-unknown-linux-musl/release:/b" alpine /b/nabu-satellite
# -> nabu-satellite skeleton
```

## Model licenses

The ONNX models under `models/` (melspectrogram, speech embedding, `ok_nabu` wake classifier)
are **CC-BY-NC-SA 4.0 (NonCommercial)** — fine for personal use, not redistributable in a
commercial product. Sources and details: [`models/LICENSE-models.md`](models/LICENSE-models.md).
