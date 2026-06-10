# nabu-satellite

A single static Rust binary that acts as a drop-in [Wyoming protocol](https://github.com/rhasspy/wyoming)
satellite **server**, replacing the EOL Python [wyoming-satellite](https://github.com/rhasspy/wyoming-satellite)
on Raspberry Pi voice satellites. Wake-word detection ("ok nabu", openWakeWord ONNX models) runs embedded
in-process via [tract](https://github.com/sonos/tract) — no Python, no ONNX Runtime, no shared-library
dependencies on the target device.

## Build

### Prerequisites

- **Rust** via [rustup](https://rustup.rs) — no manual toolchain setup needed:
  `rust-toolchain.toml` auto-installs the pinned 1.91 toolchain and the
  `aarch64-unknown-linux-musl` target on the first cargo invocation.
- **Python 3** (for the zig toolchain used by cross-builds):
  `python3 -m pip install --user cargo-zigbuild` — the `ziglang` package (which bundles
  the zig compiler) comes in as its dependency. Make sure `~/.local/bin` is on `PATH`.

### Host (dev)

```sh
cargo build
cargo test --release   # the wake-pipeline spike runs real tract inference; release keeps it fast
```

### Cross build (Raspberry Pi, fully static aarch64-musl)

One command (make sure `~/.local/bin` is on `PATH` per the prerequisites above):

```sh
satellite/scripts/build-release.sh
```

Verified working toolchain:

| Component | Version |
|---|---|
| rustc | 1.91.1 (`rust-toolchain.toml` pins 1.91 + the `aarch64-unknown-linux-musl` target) |
| cargo-zigbuild | 0.22.3 (`python3 -m pip install --user cargo-zigbuild`) |
| zig | 0.16.0, provided by the `ziglang` pip package (`python3 -m ziglang version`) |

**Caveat — tract-linalg vs zig cc:** `tract-linalg` 0.23 compiles its SVE f16 C kernels with
`-march=armv8.2-a+sve+fp16`. zig cc rejects GCC's `fp16` extension name
(`error: unknown CPU feature: 'fp16'`) because LLVM/zig spell it `fullfp16`. A plain
`cargo zigbuild --target aarch64-unknown-linux-musl --release` therefore fails in
`tract-linalg`'s build script. The fix is a tiny CC shim that rewrites the feature name and
delegates to the same `zig cc` (what cargo-zigbuild's generated wrapper runs) — checked in at
[`scripts/zigcc-fp16-shim.sh`](scripts/zigcc-fp16-shim.sh). `build-release.sh` wires it up via
`CC_aarch64_unknown_linux_musl` (cc-rs uses the shim for C code; Rust code and linking still
go through cargo-zigbuild's own wrappers).

Output: `target/aarch64-unknown-linux-musl/release/nabu-satellite` —
`ELF 64-bit LSB executable, ARM aarch64, version 1 (SYSV), statically linked, stripped`,
**18,608,160 bytes (17.7 MiB)** (fat LTO, `strip = true`; the full tract-onnx inference stack
plus the three `include_bytes!`-embedded wake models — ~2.6 MB of ONNX — account for the
growth over the 1.2 MiB pre-tract skeleton).
Execution verified on arm64 via Docker binfmt emulation (no Pi needed):

```sh
docker run --rm --platform linux/arm64 \
    -v "$PWD/target/aarch64-unknown-linux-musl/release:/b" \
    alpine /b/nabu-satellite --listen 0.0.0.0:10700 --no-button --no-led
# -> nabu-satellite listening on 0.0.0.0:10700 (hub dials in)
```

## Status LED

The satellite lights an LED while a voice interaction is active (turn start → end of TTS
playback; announcements too). Default: the reSpeaker 2-Mic HAT's 3 onboard APA102 LEDs via
`/dev/spidev0.1` — requires `dtparam=spi=on` in `/boot/firmware/config.txt` and the `spi`
group (the [systemd unit](deploy/nabu-satellite.service) already adds it). Alternatives: `--led-gpio <pin>` for a single wired
LED (BCM numbering, pin → ~330 Ω → LED → GND), or `--no-led`. Missing LED hardware is not an
error — the satellite logs one warning and runs without it.

## Testing on the WSL dev host

Run the native release binary against the real hub (hub config dials `tcp://<host>:10800`):

```bash
cd satellite && RUST_LOG=info ./target/release/nabu-satellite \
  --listen 0.0.0.0:10800 \
  --no-button \
  --no-led \
  --mic-command 'parecord --raw --rate=16000 --format=s16le --channels=1 | python3 -u -c "
import sys, audioop
r, w = sys.stdin.buffer, sys.stdout.buffer
while b := r.read1(4096):
    w.write(audioop.mul(b, 2, 3.0))
"' \
  --snd-command 'paplay --raw --rate=22050 --format=s16le --channels=1 --latency-msec=50'
```

- **`--latency-msec=50` on paplay is mandatory on WSLg**: the RDP sink's default stream buffer
  adds ~1.6 s of playback latency per stream (measured: a 0.5 s clip takes ~2.1 s to drain by
  default, 0.51 s with the flag). Without it every TTS reply/chime/cue plays ~1.6 s late and the
  hub's follow-up window (whose timing model assumes near-zero sink latency, compensated only by
  `FollowUp:PlaybackTailMs` = 400 ms) is perceptually swallowed by the lag.
- The python pipe applies 3x mic gain (`audioop.mul`, saturating): WSLg's mic bridge arrives
  quiet and the binary has no gain flag. Tune the `3.0` or drop the pipe on a proper mic.
- On a Pi with direct ALSA (`aplay -D plughw:...`) the sink buffer is far smaller; the flag is a
  PulseAudio-ism and does not apply.

## Model licenses

The ONNX models under `models/` (melspectrogram, speech embedding, `ok_nabu` wake classifier)
are **CC-BY-NC-SA 4.0 (NonCommercial)** — fine for personal use, not redistributable in a
commercial product. Sources and details: [`models/LICENSE-models.md`](models/LICENSE-models.md).
