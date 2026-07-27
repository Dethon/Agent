# XVF3800 LED Ring as the Satellite's Activity Indicator

**Date:** 2026-07-27
**Status:** Implemented and validated on hardware (Tasks 1-8, including the phase-semantics addendum)

## Problem

The deployed satellite (`fran-office-01`, a reSpeaker XVF3800 USB 4-Mic Array + HiFiBerry
MiniAmp) has **no activity indicator at all**. `LedConfig` defaults to `None`, and the two
backends that exist — `--led-gpio <pin>` (a wired indicator) and `--led-spi` (the reSpeaker
2-Mic HAT's 3 APA102s) — target hardware this unit does not have. They are dead code in
production: the 2-Mic HAT is only a documented override path, and no unit is provisioned
with it.

Meanwhile the XVF3800 carries **12 WS2812 addressable RGB LEDs** that the satellite never
touches. Because nothing configures them, they sit in the device's power-on default —
rainbow for 2 s, then direction-of-arrival mode — **lit 24/7**, conveying nothing about
whether the satellite is idle, listening, thinking, or speaking.

## Decision summary (user-approved)

- **Use the XVF3800 ring**; delete the GPIO and SPI backends outright rather than keeping
  them alongside.
- **A distinct look per phase**, not the current off/on policy.
- **One blue family** — phase is read from *motion* (steady pointer vs. breathing vs. solid),
  not from hue, so the ring reads as one device rather than a traffic light.
- **Native USB control transfers** via the pure-Rust `nusb` crate — not shelling out to the
  vendor's `xvf_host` binary.
- **Enabled by default** when the device is present; `--no-led` disables.

## Verified facts

Everything below was read off the live unit (`dethon@192.168.5.11`) before designing, not
inferred from documentation.

**The ring is exposed as device-control parameters on resource `0x14`:**

| Command | id | payload | stock value |
|---|---|---|---|
| `LED_EFFECT` | `0x0c` | u8 — 0=off, 1=breath, 2=rainbow, 3=single, 4=DoA | `4` |
| `LED_BRIGHTNESS` | `0x0d` | u8 — breath/rainbow only | `127` |
| `LED_GAMMIFY` | `0x0e` | u8 | `1` |
| `LED_SPEED` | `0x0f` | u8 — breath/rainbow only | `8` |
| `LED_COLOR` | `0x10` | u32 LE — breath and single modes | `0x002040` |
| `LED_DOA_COLOR` | `0x11` | 2× u32 LE — base, then DoA | `0x002040`, `0x00C066` |

**The stock DoA colours are already the requested look**: dim blue base (`0x002040`) with a
green pointer (`0x00C066`). The design still pins them at init — a reflash or another tool
would otherwise silently change the look.

**The wire protocol**, decoded from a `usbmon` capture of `xvf_host` issuing LED commands:

```
write:   bmRequestType 0x40, bRequest 0x00, wValue = cmd,        wIndex = 0x0014, data = <LE payload>
status:  bmRequestType 0xC0, bRequest 0x00, wValue = cmd,        wIndex = 0x0014, len = 1      -> 0x00 = OK
read:    bmRequestType 0xC0, bRequest 0x00, wValue = cmd | 0x80, wIndex = 0x0014, len = 1 + n
```

Recipient is **device**, not interface — `wIndex` carries the resource id, not an interface
number — so the kernel requires no interface claim for these transfers.

**Interface 3 (`reSpeaker Control`) is vendor-class with zero endpoints** — pure ep0 control
traffic, no kernel driver bound, no contention with the UAC audio interfaces. Verified by
issuing LED commands while the satellite was live and streaming.

**Transfers complete in ~250 µs** (usbmon timestamps), so blocking calls from the render task
are as harmless as the existing blocking `spi.write()`.

**Permissions are the only blocker**: `/dev/bus/usb/001/003` is `crw-rw-r-- root root`, so the
service user has read but not write, and `xvf_host` as that user fails with "Failed to open
device". The service process already carries gid 46 (`plugdev`) — systemd applies the user
database's supplementary groups on top of `SupplementaryGroups=` — so a udev rule granting
`plugdev` write access is sufficient, with no `sudo` anywhere.

## Design

### Backend

`LedBackend` collapses to two variants:

```rust
enum LedBackend {
    Xvf3800(nusb::Device),
    #[cfg(test)] Probe(Arc<Mutex<Vec<(u16, Vec<u8>)>>>),
}
```

`apa102_frame()`, `rppal::spi`, and `LedConfig::{Gpio, Spi}` are deleted. `rppal` remains a
dependency — the GPIO button still uses it.

The wire encoding is a pure function so it is testable without hardware, mirroring the
existing `apa102_frame_golden_bytes` idiom:

```rust
const RESID_LED: u16 = 0x14;
const LED_EFFECT: u16 = 0x0c;
const LED_BRIGHTNESS: u16 = 0x0d;
const LED_SPEED: u16 = 0x0f;
const LED_COLOR: u16 = 0x10;
const LED_DOA_COLOR: u16 = 0x11;

fn write_setup(cmd: u16, payload: &[u8]) -> ControlOut  // 0x40 / 0x00 / cmd / RESID_LED
```

Device lookup is `nusb::list_devices()` filtered on `2886:001a`, then `.open()`. Blocking
transfer variants are used, keeping the backend a plain synchronous object like the rppal
ones it replaces.

Failure policy, unchanged from today: **LED problems never take the satellite down.**

- Device absent → no LED, **no warning**. Unlike GPIO/SPI pins, USB is discoverable, so "not
  plugged in" is not an error worth logging.
- **USB enumeration failing outright** → also no LED, no warning. The WSL dev host has no
  `/sys/bus/usb/devices/`, so `nusb::list_devices()` returns `Err` rather than an empty
  iterator; "no USB subsystem" is the same class of absent as "empty bus", and warning on it
  would mean a spurious line on every dev-satellite start.
- Open failure on a device that **is** present → one warning. This is the actionable case
  (missing udev rule / permissions), so unlike the two above it must be visible.
- Write failure → one warning, LED disabled for the rest of the connection; the next
  connection re-initializes.

### Phase mapping

Init runs once per backend build; each transition is then one or two transfers:

```
init       LED_BRIGHTNESS 127 ; LED_SPEED 8
           LED_DOA_COLOR 0x002040, 0x00C066 ; LED_EFFECT 0

Idle       LED_EFFECT 0                              (dark)
Listening  LED_EFFECT 4                              (blue ring + green DoA pointer)
Thinking   LED_COLOR 0x002040 ; LED_EFFECT 1         (breathing blue)
Speaking   LED_COLOR 0x0040A0 ; LED_EFFECT 3         (solid blue)
```

Colour is written **before** effect, so the previous colour never flashes in the new mode.
The last-written colour is cached and a redundant `LED_COLOR` skipped, preserving the current
"writes only on transitions" invariant.

`LED_EFFECT 0` also clears the power-on rainbow/DoA state at init. `LED_GAMMIFY` is left at
whatever the device holds (stock `1`) — gamma correction is a device-wide preference, not a
per-phase one.

The 120 s Thinking fallback survives unchanged — if a reply never arrives after a transcript,
the ring stops glowing, with "dark" now meaning `LED_EFFECT 0`.

**Four constants are starting points to be tuned on-device, not final values**:
`LED_BRIGHTNESS 127` and `LED_SPEED 8` (the device's own stock values) and the two colours
`0x002040` / `0x0040A0`. `LED_BRIGHTNESS` and `LED_SPEED` affect only breath mode, and
`LED_COLOR` is brightness-modulated there, so the Thinking look cannot be predicted from the
constants alone; Speaking must likewise sit visibly brighter than the DoA base without
blazing. Implementation ships these values, then adjusts them by eye over SSH against the real
ring before the change is considered done.

**Every write is followed by its status read** (`0xC0 / cmd / len 1`, `0x00` = OK), which is
what `xvf_host` itself does. This doubles the transfers to ~500 µs per parameter — irrelevant
at this scale — and is load-bearing for the failure policy above: without it, a rejected
command or a wrong id would fail silently instead of disabling the LED with a warning.

### Lifecycle

The render task stays **per-connection**, exactly as today: the state machine publishes
`LedState` on a watch channel, `spawn_led` owns the backend, and `LedGuard`'s drop aborts the
task so the backend's `Drop` blanks the ring on connection end or supersede.

Two additions, because the ring's power-on default is lit and the per-connection task cannot
cover the gaps around it:

- **Blank at process start** in `main.rs`, closing the boot → first-hub-connect window.
- **Blank on graceful shutdown**, so stopping the service does not leave the ring lit — which
  is precisely the 24/7-lit behaviour this work removes. `main.rs` currently has **no signal
  handling at all** — its accept loop runs forever and the process dies on SIGTERM — so this
  adds a `tokio::signal` SIGTERM/SIGINT race against the accept loop. The `signal` feature is
  already enabled in `Cargo.toml` and unused, so no dependency change is needed.

Both are best-effort: a failure is logged at debug and ignored. Both are **skipped entirely
under `--no-led`** — that flag means "this process does not touch the ring", so a satellite
started with it leaves whatever the device was already showing untouched.

`led_tx` has a second subscriber — `duck_rx`, which drives music ducking. `LedState`'s
semantics are untouched by this change, and a satellite running LED-less still ducks
correctly.

### Flag surface

`--led-gpio <pin>` and `--led-spi` are removed. `--no-led` stays.

The ring is **enabled by default** when `2886:001a` is present. The opt-in flag that GPIO and
SPI required bought discoverability the USB bus provides for free, and defaulting on means the
deployed unit needs no `ExecStart` change — neither the voice-only path nor the music drop-in
that overrides it.

`LedConfig` therefore collapses to a two-state enum (`Auto` / `None`).

### Deployment

`scripts/provision-satellite-rs.sh` extends the existing rule in
`/etc/udev/rules.d/99-nabu-usb-audio.rules`:

```
ACTION=="add", SUBSYSTEM=="usb", ATTR{idVendor}=="2886", ATTR{idProduct}=="001a", \
  ATTR{power/control}="on", MODE="0660", GROUP="plugdev"
```

then runs `udevadm control --reload-rules && udevadm trigger` — the rule fires only on `add`,
so an already-enumerated device needs an explicit trigger.

`plugdev` is added to the unit's `SupplementaryGroups=` explicitly. The deployed user already
has it via the user database, but a fresh Pi user should not depend on distro group defaults.

Docs updated in the same change: the LED invariant in `satellite/CLAUDE.md`, the LED section
of `satellite/README.md`, and the header comments in `satellite/deploy/nabu-satellite.service`
and `scripts/provision-satellite-rs.sh` that reference `--led-spi` / `--led-gpio`.

### Testing

TDD throughout — failing test first, watched to fail, then implementation.

- **Golden-byte tests** pin the control setup packets against the captured traffic, the same
  way `apa102_frame_golden_bytes` pinned the APA102 frame.
- **A per-state command-sequence test** pins the phase mapping, including colour-before-effect
  ordering and the skipped redundant `LED_COLOR`.
- **An init-sequence test** pins the brightness/speed/DoA-colour/blank ordering.
- **The existing render-loop tests carry over** against the upgraded `Probe` backend
  (transition-only writes, the 120 s Thinking fallback going dark and relighting on a late
  reply, sender-drop blanking).
- **Config tests** for the collapsed flag surface.
- **On-device validation over SSH**: a real turn showing off → DoA → breath → solid → off,
  plus the boot and shutdown blanks.

## Risks

~~**`nusb` must cross-compile for `aarch64-unknown-linux-musl`.**~~ **Retired** — verified
before planning. `nusb` 0.2.5 builds clean under `cargo zigbuild --target
aarch64-unknown-linux-musl --release`, producing a **statically linked** binary, and pulls
only pure-Rust dependencies (`rustix`, `linux-raw-sys`, `libc`, `log`, `slab`, `futures-core`,
`bitflags`, `once_cell`) — no C toolchain involvement, exactly the property `Cargo.toml`
curates for. The `Device::control_out` / `Device::control_in` signatures used throughout this
spec were compiled against the real crate, not inferred from documentation. The raw
`USBDEVFS_CONTROL` ioctl fallback is therefore not needed.

Note that nusb's blocking API is `MaybeFuture`-based: calls take a timeout and are driven to
completion with `.wait()`, e.g.
`dev.control_out(ControlOut { .. }, Duration::from_millis(200)).wait()?`.

**Command IDs are hardcoded.** They are the vendor tool's own ABI and stable across the 2.0.6
→ 2.0.10 upgrade this unit already survived, but a future firmware could in principle
renumber them. A wrong id writes to some other resource-`0x14` parameter, so the on-device
validation pass is what catches it.

**Deleting the GPIO/SPI backends removes the 2-Mic HAT's LED.** No deployed unit uses it, and
the HAT override path keeps its button (`--button-gpio 17`); only its 3 APA102s go dark. This
is the accepted cost of the smaller, single-purpose render loop.

---

## Addendum: phase semantics correction (2026-07-27, post-deployment)

On-device validation of the deployed build found the ring **never returns to dark** after the
first wake. The permission fix and the ring driving itself both worked; the defect is in what
the satellite's four `LedState` values have always *meant*.

### Evidence

A 0.4 s sampler on the live unit, correlated against the journal (only two wakes fired, at
19:02:00 and 19:02:48):

```
19:02:00  wake         -> effect 4   listening
19:02:12               -> effect 3   speaking   (the reply)
19:02:14               -> effect 4   listening  (drain, mode still Streaming)
19:02:17  transcript   -> effect 1   "thinking" (breathes 31 s, AFTER the reply)
19:02:48  wake         -> effect 4   (next turn)
```

`effect 0` never recurs. Across 400 further samples the ring stayed lit.

### Root cause

`transcript` is not an STT result — the hub emits it with permanently empty text from exactly
two places (`WyomingSatelliteHost.cs:141` arbitration re-arm, and `:329` `EndConversation`).
It is the **turn-end** signal, as `satellite/CLAUDE.md` already documented ("ends its turn and
re-arms wake"). But `state_machine.rs:278` maps it to `LedState::Thinking`, so the ring starts
breathing when the turn *finishes* and only clears via the 120 s `THINKING_FALLBACK`.

Under the old binary on/off policy this mislabel was invisible — `Thinking` and `Listening`
both meant "lit". Giving each phase a distinct look exposed it. No deployed unit had an LED
before, so it was never observed.

### The missing signal

The hub→satellite vocabulary is exactly five events: `run-satellite`, `transcript`,
`pause-satellite`, `audio-start`, `audio-stop`. **None marks the end of the user's speech.**
The hub endpoints internally (`CloseCapture` → `MarkSpeechEnd`) and tells the satellite
nothing, so "Listening" necessarily runs until the reply starts and Thinking is unreachable.

### Decision (user-approved)

Thinking means: **from the moment the user stops speaking until the turn ends, excluding the
stretches where the agent is talking.** Implementing it needs a new event.

- The hub emits **`voice-stopped`** (the standard Wyoming VAD event name, not an invented one)
  when a capture closes and will actually be transcribed.
- **It is emitted after the outcome checks, not at `CloseCapture` itself.** An
  arbitration-abandoned or unknown-voice capture must not flash Thinking — arbitration losers
  are specified to stand down silently with the LED going to Idle.
- The satellite maps `voice-stopped` → `Thinking` and, correcting the defect, `transcript` →
  `Idle`.

Corrected mapping:

```
wake / run-pipeline   -> Listening  (effect 4, blue ring + green pointer)
voice-stopped         -> Thinking   (effect 1, breathing blue)
audio-start           -> Speaking   (effect 3, solid blue)
drain, mode Streaming -> Listening  (follow-up window: the mic is live again)
transcript            -> Idle       (effect 0, dark)
```

The `drain → Listening` rule stays: after a reply drains with the capture still open, the
follow-up window genuinely has the mic live, so the blue+green pointer is correct there.

`PROTOCOL_VERSION` goes 1.3 → 1.4 on both sides (`satellite/src/wyoming/event.rs:3`,
`McpChannelVoice/Services/WyomingProtocol/WyomingWriter.cs:12`). The change is compatible in
both directions: the satellite already answers unknown events with
`other => warn!("ignoring event {other}")`, and a new satellite against an old hub simply
never sees `voice-stopped`, degrading to no breathing phase rather than breaking.

### Also corrected here

The plan's Task 5 Step 2 gave the deploy as a bare
`./scripts/provision-satellite-rs.sh dethon@192.168.5.11`. That is wrong for this unit:
provisioning **unconditionally deletes** the music drop-in
(`scripts/provision-satellite-rs.sh:239`) and only recreates it when `MUSIC_HUB` is set, and
its `THRESHOLD` default of 0.7 would overwrite the unit's tuned 0.6. The correct invocation
preserves the live values:

```bash
MUSIC_HUB=192.168.5.45 MUSIC_ROOM=fran_s_office THRESHOLD=0.6 TRIGGER_LEVEL=2 TTS_VOLUME=75 \
  ./scripts/provision-satellite-rs.sh dethon@192.168.5.11
```

### On-device tuning outcome (2026-07-27)

The "Phase mapping" section above ships `LED_BRIGHTNESS 127`, `LED_SPEED 8`, and Thinking
colour `0x002040` — the device's own stock values, explicitly labelled "starting points to be
tuned on-device, not final values". Tuning by eye against the deployed unit changed all three:

| Constant | Starting point | Shipped |
|---|---|---|
| `LED_BRIGHTNESS` (breath) | `127` | `255` |
| `LED_SPEED` (breath) | `8` | `1` |
| Thinking colour | `0x002040` | `0x0000FF` |

The key finding is **why**, not just the new numbers: breath mode **multiplies** its colour by
`LED_BRIGHTNESS` rather than using it as an independent cap, so a pre-dimmed Thinking colour
(`0x002040`, the same dim blue used for the DoA base) renders as no visible pulse at all at
`LED_BRIGHTNESS 127` — the multiplied result was too dark to see. Fixing this needed both a
saturated colour (`0x0000FF`) and full brightness (`255`) together; either alone still looked
off. Separately, `LED_SPEED 8` (the stock rainbow-mode rate) read as too brisk for a "thinking"
pulse — `1` is the slowest value that still animates (`0` stops the animation outright, i.e.
freezes the breath at a fixed level rather than pulsing).

Shipped in `satellite/src/led.rs` as `BREATH_BRIGHTNESS`, `BREATH_SPEED`, and `THINKING_COLOR`.
