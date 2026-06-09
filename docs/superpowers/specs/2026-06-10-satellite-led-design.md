# Satellite LED Support — Design Spec

**Date**: 2026-06-10
**Status**: Approved for planning
**Owner**: Francisco Crespo
**Extends**: `docs/superpowers/plans/2026-06-09-rust-wyoming-satellite.md` (adds a V1 feature; no existing milestone changes)

## Goal

The `nabu-satellite` binary lights an LED **while the satellite is active** — steady on from the moment a turn starts (wake word or button) until the reply's TTS playback finishes, and also while a standalone announcement plays. The LED is **optional hardware**: a device with no LED connected runs identically, with a single warning at connect time.

## Decisions (resolved with the owner)

1. **Semantics**: one steady on/off signal covering the *whole interaction* — listening → thinking → speaking. Announcements (TTS with no preceding turn) also light it.
2. **Hardware paths in V1**: the reSpeaker 2-Mic HAT's 3 onboard APA102 LEDs (SPI), and a plain single LED on any free GPIO pin. Jabra Speak LEDs are confirmed uncontrollable on Linux (stateful HID telephony, same trap as its buttons) — out of scope.
3. **Default**: `LedConfig::Spi` (the HAT's APA102s), consistent with every other default targeting the reSpeaker HAT (mic/snd commands, button GPIO 17). Opt out with `--no-led`; switch with `--led-gpio <pin>`. Missing hardware degrades gracefully.
4. **Architecture**: approach C — the state machine publishes semantic states on a `tokio::sync::watch` channel; a dedicated per-connection LED task renders them to hardware. Chosen for evolution: per-phase blink patterns later touch only the render task.

## Verified hardware facts (constrain the design)

- The 2-Mic HAT carries **exactly 3 APA102-2020 LEDs** daisy-chained on **SPI0** — data GPIO10/MOSI, clock GPIO11/SCLK, **no chip-select wired**, and **no LED power-enable GPIO** (the GPIO5 power-enable idiom belongs to the 4-Mic Array; wyoming-satellite's `2mic_service.py` `LEDS_GPIO=12` setup is a verified no-op on this HAT). Sources: Seeed wiki + v1/v2 schematics; `respeaker/mic_hat` `pixels.py`/`apa102.py`.
- Reference drivers open **`/dev/spidev0.1` at 8 MHz, SPI mode 0, MSB first**. Requires `dtparam=spi=on` (`/boot/firmware/config.txt` on Bookworm).
- APA102 wire format: 32-bit zero start frame; per-LED frame `0xE0 | brightness(5-bit)` + 3 color bytes (B, G, R); end frame ≥ N/2 clock pulses — a trailing 32-bit zero frame satisfies 3 LEDs and doubles as the SK9822 latch. **A full 3-LED update is 20 bytes.**
- `rppal` 0.22 (already a dependency) includes an ioctl-based `spi` module over `/dev/spidevB.S`, libc-only, **static-musl clean**. No new crates needed.
- rppal `OutputPin` resets (LED off) on drop by default; the reset does not run on abnormal termination, so init must clear stale state.
- The Wyoming protocol defines **no LED events**; LED state derives entirely from the satellite's own state machine. Zero protocol changes.

## Architecture

```
state_machine.rs ── watch::Sender<LedState> ──▶ led task (per connection, AbortOnDrop)
                                                   │ renders state → backend
                                                   ▼
                                  LedBackend::Gpio(OutputPin) | ::Spi(rppal Spi) | ::Probe (test)
```

A new `src/led.rs` module owns everything LED:

```rust
pub enum LedState { Idle, Listening, Thinking, Speaking }
pub enum LedConfig { None, Spi, Gpio(u8) }   // default: Spi
```

- `spawn_led(cfg, watch::Receiver<LedState>) -> Option<LedGuard>` mirrors `gpio.rs::spawn_button`: `None` config → no task; init failure → `warn!` → no task; otherwise a tokio task owns the backend and is aborted via guard drop when the connection ends (same `AbortOnDrop` idiom as the pumps).
- The **state machine never knows what hardware exists** — it publishes semantics; a connection without an LED still publishes (sends to a dropped receiver are ignored).
- **V1 render policy** (entirely inside the led task): `Idle → off`, anything else → steady on, all 3 HAT LEDs, one fixed color/brightness (module constants: RGB `(0, 0, 255)` at APA102 global brightness `8` of 31; a `--led-color` flag is explicitly deferred).

### Publish points (in `state_machine.rs`)

| Event | Published state |
|---|---|
| `start_turn` (wake or button) | `Listening` |
| `transcript` while Streaming | `Thinking` |
| `audio-start` | `Speaking` (covers announcements too) |
| `audio-stop` (after `finish()`) | `Idle` |
| stale `transcript` while Idle | no change |
| connection end / supersede | task dropped → backend `Drop` → off |

Init writes one explicit "off" frame to clear leftovers from a crashed predecessor.

**Thinking fallback**: if the hub never sends reply audio after a transcript (hub error/timeout — a known deferred race), the led task itself falls back to dark after **120 s** in `Thinking` (matches the hub's reply timeout). This is a display policy, so it lives in the render task, not the state machine.

## Config & CLI

`LedConfig` lives in `config.rs` beside `ButtonConfig`. Flags: `--no-led` | `--led-gpio <pin>`. No `--led-spi` flag — it's the default and `--led-gpio`'s absence restores it.

## Backends

- **`Gpio(OutputPin)`** — `set_high()`/`set_low()`; rppal reset-on-drop turns it off on release.
- **`Spi(rppal::spi::Spi)`** — `Spi::new(Bus::Spi0, SlaveSelect::Ss1, 8_000_000, Mode::Mode0)`. A pure function `apa102_frame(rgb: (u8,u8,u8), brightness: u8, n: usize) -> Vec<u8>` encodes the update; the backend just writes it. `Drop` writes the off frame best-effort.
- **`#[cfg(test)] Probe`** — records writes for render-logic tests.

## Error handling

- Init failure (no `/dev/spidev0.1`, GPIO claim fails) → one `warn!`, satellite runs LED-less. Never fatal.
- Write failure mid-connection → one `warn!`, backend disabled for the rest of the connection (no log spam); the next connection re-initializes.
- LED problems never tear down a connection — it's cosmetic hardware.

## Testing (all CI-safe)

1. **`apa102_frame` golden bytes** — exact 20-byte vector for 3 LEDs: zero start frame, `0xE0|brightness` mask, B-G-R order, zero end frame.
2. **Config parsing** — default `Spi`; `--led-gpio 22`; `--no-led`.
3. **State publication** — drive `start_turn`/`handle_hub_event` and assert the `watch::Receiver` sees Listening → Thinking → Speaking → Idle; announcement-only path (Idle → Speaking → Idle); stale-transcript noop.
4. **Render task with `Probe`** — on/off transitions; 120 s Thinking fallback under `tokio::time::pause`.
5. `LedConfig::None` → `spawn_led` yields no task (mirrors the button's None test).

Hardware validates on-device in Milestone 5, like the button.

## Deployment

Provisioning notes (README/systemd unit) gain:
- `dtparam=spi=on` in `/boot/firmware/config.txt` (HAT builds).
- `SupplementaryGroups=spi` next to the existing `gpio` group (spidev nodes are `root:spi` on Raspberry Pi OS).
- Jabra/headless builds: `--no-led`, or `--led-gpio <pin>` with a wired indicator (BCM pin → ~330 Ω → LED → GND).

Identical across Pi models (Zero 2 W or the Pi 4 chosen in the media-player spec) — SPI0 and BCM numbering are unchanged.

## Out of scope

- Per-phase blink/color patterns (architecture supports them; render task only).
- `--led-color`/brightness flags (constants for now).
- Jabra LED control; sysfs ACT-LED backend (root-owned sysfs, kernel 6.12.62 regression on Zero 2 W, invisible in a case).
- Wyoming protocol changes (none needed).
