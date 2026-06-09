#!/bin/sh
# zig cc rejects GCC's `+fp16` -march extension name; LLVM/zig spell it `fullfp16`.
# tract-linalg hardcodes `-march=armv8.2-a+sve+fp16` for its SVE f16 kernels, so
# rewrite the feature name and delegate to `zig cc` exactly like cargo-zigbuild's
# generated wrapper does (`cargo-zigbuild zig cc --` invokes the zig C compiler
# bundled in the `ziglang` pip package).
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
