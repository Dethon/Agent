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
**19,674,944 bytes (18.8 MiB)** (fat LTO, `strip = true`, `-C target-cpu=cortex-a53` with
`-aes,-sha2` subtracted — the Pi's BCM2837/RP3A0 lacks the crypto extensions; the binary is
objdump-verified to contain no AES/SHA instructions. The full tract-onnx inference stack plus
the three `include_bytes!`-embedded wake models — ~2.6 MB of ONNX — account for the growth
over the 1.2 MiB pre-tract skeleton).
Execution verified on arm64 via Docker binfmt emulation (no Pi needed):

```sh
docker run --rm --platform linux/arm64 \
    -v "$PWD/target/aarch64-unknown-linux-musl/release:/b" \
    alpine /b/nabu-satellite --listen 0.0.0.0:10700 --no-wake
# -> nabu-satellite listening on 0.0.0.0:10700 (hub dials in)
```

`--no-wake` is required **under qemu only**: qemu-user advertises ARMv8.2 fp16 hwcaps, so
tract-linalg activates f16 assembly kernels that crash under emulation during the boot-time
model load. A real Pi A53 has no fp16 hwcap and auto-selects the Cortex-A53 f32 kernels
(`RUST_LOG=tract_linalg=info` shows `CPU optimisation: CortexA53` at startup).

## Hardware defaults

Compiled-in defaults target a **Jabra Speak2 (55/75)** on USB: audio via `plughw:0,0`
(provisioning pins `snd_usb_audio` to ALSA index 0 — the card *name* is model/variant-dependent:
75 → `J75`, 55 MS → `MS`, 55 UC → `UC`, so the index-pinned device is baked in; confirm yours
with `arecord -L`), no button (the Jabra's onboard buttons are HID-telephony, unusable on
Linux), no LED. The Speak2's 48 kHz native rate is resampled by `plughw` in both directions.
For a reSpeaker 2-Mic HAT pass `--mic-command`/`--snd-command` with
`plughw:CARD=seeed2micvoicec,DEV=0`, plus `--button-gpio 17` and `--led-spi`.

The default audio commands carry latency-tuned ALSA flags — keep them when overriding devices:

- `arecord … -F 20000` (20 ms capture period): the alsa-utils default is buffer/4 = 125 ms
  periods, which delays every mic sample by up to 125 ms on both the wake and speech→STT path.
- `aplay … --start-delay=100000 -F 50000`: aplay's default start threshold is the full 500 ms
  buffer, so a streamed TTS reply wasn't audible until 500 ms of audio had been
  synthesized+delivered; playback now starts at ~100 ms queued (the 500 ms buffer stays for
  underrun headroom).

## Status LED

The satellite lights an LED while a voice interaction is active (turn start → end of TTS
playback; announcements too). Default: none (a Jabra has no controllable LED). `--led-spi`
drives the reSpeaker 2-Mic HAT's 3 onboard APA102 LEDs via `/dev/spidev0.1` — requires
`dtparam=spi=on` in `/boot/firmware/config.txt` and the `spi` group (the
[systemd unit](deploy/nabu-satellite.service) already adds it). `--led-gpio <pin>` drives a single wired
LED (BCM numbering, pin → ~330 Ω → LED → GND). Missing LED hardware is not an
error — the satellite logs one warning and runs without it.

## Testing on the WSL dev host

Run the native release binary against the real hub (hub config dials `tcp://<host>:10800`):

```bash
cd satellite && RUST_LOG=info ./target/release/nabu-satellite \
  --listen 0.0.0.0:10800 \
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
