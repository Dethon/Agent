# nabu-satellite

Standalone Rust crate (NOT in the .NET solution): a fully static aarch64-musl Wyoming satellite binary (~18.8 MiB) for Raspberry Pi, embedding the openWakeWord "ok nabu" pipeline (melspectrogram → embedding → classifier ONNX, run in-process via tract) and the cue WAVs. `README.md` covers build prerequisites, hardware defaults, the LED, and model licenses; this file holds the invariants that must not be broken.

## Key Files

| What | Where |
|------|-------|
| CLI flags & defaults | `src/config.rs` |
| State machine | `src/satellite/state_machine.rs` |
| Wake detector | `src/wake/detector.rs` |
| Wyoming codec | `src/wyoming/{codec,event}.rs` |
| Audio (mic/playback/cues) | `src/audio/*.rs` |
| LED & button | `src/led.rs`, `src/gpio.rs` |
| Build & deploy | `scripts/*.sh`, `deploy/nabu-satellite.service`, repo-root `scripts/provision-satellite-rs.sh`, repo-root `scripts/wsl-satellite*.sh` |

## Invariants

- **The satellite is the Wyoming SERVER; the hub dials in** (default `--listen 0.0.0.0:10700`). A new hub connection supersedes the previous one (abort + await) so a dead-peer TCP wedge can't hold the exclusive `plughw` mic for the ~15-min retransmission timeout. The three ONNX models are parsed + optimized ONCE at boot (`WakeModels::load`, fail-fast) and shared across connections — re-arm after a reconnect is instant.
- **Cancellation safety**: hub/mic reads AND playback writes/drains are multi-await compound I/O, NOT `select!`-safe; they run in dedicated pump tasks (hub, mic, playback) feeding bounded mpsc channels, and the main `select!` only races `recv()` futures.
- **Playback pump**: the pump task is the single owner of the playback device. `audio-stop`'s drain (~0.5-2 s of buffered TTS) happens inside the pump, so wake/button/mic stay live during the reply tail; drain completions return on an unbounded channel (bounded would AB-deadlock) carrying a generation that gates the LED Idle/Listening transition (a stale completion can't blank a newer stream); playback errors stay connection-fatal; cues route through the pump too, so a cue player can never EBUSY-race a reply for the exclusive device (cues are dropped while a stream is active).
- **Audio contract**: mic = 16 kHz mono S16LE in 1280-sample/80 ms chunks (arecord subprocess; bytes end-to-end internally, decoded to i16 only at the detector); playback sink = FIXED 22 050 Hz mono S16LE (aplay) that ignores announced rates — everything it plays must be 22 050 Hz: hub-side TTS and chime plus the embedded cue WAVs.
- **ALSA latency flags**: the default commands carry `arecord … -F 20000` (20 ms periods; the alsa-utils default of buffer/4 = 125 ms delayed every mic sample on the wake and STT paths) and `aplay … --start-delay=100000 -F 50000` (start at ~100 ms queued instead of the full-500 ms-buffer default; buffer stays 500 ms for underrun headroom). Keep them when overriding devices. Plain-argv audio commands exec directly (no `sh -c`), so kill/supersede SIGKILLs aplay/arecord themselves; shell-shaped commands (WSL gain pipe) still go through sh.
- **Zero-lag pre-roll**: while idle, mic chunks fill a pre-roll ring (`--preroll-ms`, default 1000); a wake trigger flushes only the detection gap (3 chunks ≈ 240 ms), never the wake word itself; a button press flushes the full ring.
- **LED**: the state machine publishes `LedState` (Idle/Listening/Thinking/Speaking) on a tokio watch channel; a per-connection render task owns the backend (`--led-gpio` pin or `--led-spi` for the ReSpeaker HAT APA102s on `/dev/spidev0.1`); Idle→off, everything else→steady on, 120 s Thinking fallback mirroring the hub reply timeout; missing/failing LED hardware is never fatal. Idle after a reply still means actual-playback-complete (drain-completion-driven).
- **Defaults target a Jabra Speak2** on `plughw:0,0` (index-pinned via `snd_usb_audio index=0` because the ALSA card name varies by variant: 75→J75, 55 MS→MS, 55 UC→UC), no button/LED — the Jabra's buttons/LEDs are HID-telephony, unusable on Linux. The ReSpeaker 2-Mic HAT is the override path: `plughw:CARD=seeed2micvoicec,DEV=0` on both audio commands plus `--button-gpio 17 --led-spi` (needs `dtparam=spi=on` and the `spi` group).
- **Wire format**: frames are encoded as one contiguous buffer with event `data` sent once as the `data_length` body (the hub's reader prefers the body; its writer emits the same shape) — pinned by a codec test.

## Build & Deploy

`scripts/build-release.sh` cross-compiles via cargo-zigbuild + zig (the `zigcc-fp16-shim.sh` CC shim rewrites tract-linalg's `+fp16` -march feature to zig's `+fullfp16`) — never run bare `cargo zigbuild` for releases. `.cargo/config.toml` pins `-C target-cpu=cortex-a53 -C target-feature=-aes,-sha2` for the musl target (the Pi's silicon lacks the crypto extensions LLVM's cortex-a53 def would enable). Repo-root `scripts/provision-satellite-rs.sh <user@host> [mic-device]` installs the binary plus the templated `deploy/nabu-satellite.service` unit on a Pi (only dependency: `alsa-utils`; the unit pins the `performance` governor and `Nice=-10`) and, when the mic device is left at the default `plughw:0,0`, applies the Jabra ALSA/udev pinning. qemu-emulation smoke tests need `--no-wake` (qemu's fp16 hwcaps activate tract f16 kernels that crash under emulation; a real A53 selects the f32 kernels). On-device E2E validation is still open, blocked on hardware — it should also read the `RUST_LOG=debug` per-chunk "wake inference" timing line.

## Running on WSL

Repo-root `scripts/wsl-satellite.sh` builds (`cargo build --release`, native target) and runs a satellite on the WSL host through WSLg PulseAudio; the dockerized hub dials `tcp://host.docker.internal:$SAT_PORT` (defaults match `McpChannelVoice/appsettings.Development.json`: 10700 = fran-office-01, 10600 = laura-office-01). Env knobs: `SAT_PORT`, `THRESHOLD` (wake threshold, default 0.5), `MIC_GAIN` (default 3.0 via a python `audioop` pipe — WSLg's mic bridge is quiet and the binary has no gain flag; needs python ≤ 3.12), `RUST_LOG`. `paplay --latency-msec=50` is mandatory on WSLg (the RDP sink's default buffer adds ~1.6 s playback latency, so the hub opens the wake-free follow-up window — 400 ms playback-tail echo guard, then 7 s window — while the reply is still audibly playing). Stale instances are detected via an `ss` LISTEN-state check because WSL2 mirrored networking makes loopback connects to dead ports hang instead of refusing.

**WSLg's RDP audio bridge audibly degrades playback** (harsh/crackly; the Linux-side chain measures bit-clean at the Pulse monitor tap — the corruption is in the RDPSink→Windows leg). Repo-root `scripts/wsl-satellite-winaudio.sh` is the clean-audio variant: same satellite in WSL, but the audio commands are Windows binaries run through WSL interop — `ffmpeg.exe` dshow mic capture (`-audio_buffer_size 50`, the dshow analogue of arecord's `-F 20000`) and `ffplay.exe` WASAPI playback (`-af adelay=150:all=1` because every fresh playback session can randomly glitch its first instants; a 120-180 ms earcon lives entirely inside that window, so the pad moves the artifact into silence). Needs the gyan.dev ffmpeg-release-essentials zip extracted to `%LOCALAPPDATA%\nabu-satellite\`.
