# Satellite Media Player — Rust-Satellite Coexistence (dmix) Design Spec

**Date**: 2026-06-26
**Status**: Approved for planning
**Owner**: Francisco Crespo
**Supersedes (mechanism only)**: the **speech/music coexistence**, **hardware-firmware**, and **provisioning** portions of `2026-06-06-satellite-media-player-design.md`. That spec was written for the Python `wyoming-satellite` firmware and based its whole coexistence design on PipeWire `module-role-ducking` + wyoming event hooks. The fleet now runs the **Rust `nabu-satellite`** (raw ALSA, exclusive `plughw`, no Python / no PipeWire), so that mechanism does not apply. Everything else in the 2026-06-06 spec — Music Assistant + Snapcast as the media brain, the HA Music Assistant integration, the room↔`media_player` convention, voice/HA data flow, error handling, and the "why not LVA/ESPHome" decision record — **still holds and is incorporated by reference**, not repeated here.

## Goal

Turn each Rust voice satellite into a Home-Assistant-controllable **media player** so music (Spotify, radio, podcasts, local files) plays through the satellite's own speaker, ducking automatically whenever the satellite speaks — **without regressing the hard-won Jabra audio fixes** landed on `2026-06-26` (by-name USB card detection, the mic-ADC wake-tone, the keep-warm DAC handling, and the ALSA latency tuning).

Two hard requirements drive this spec:

1. **Coexistence on one speaker is mandatory.** Music and voice must share the satellite's single exclusive Jabra speaker, with music ducking under speech.
2. **The 2026-06-26 satellite audio fixes must not regress.** The design is structured so the capture (wake-tone) path is untouched and the playback path changes are minimal and independently revalidated before being trusted.

## Why the 2026-06-06 mechanism does not apply

| 2026-06-06 spec assumes (`wyoming-satellite`) | Reality (`nabu-satellite`, Rust) |
|---|---|
| PipeWire + `pipewire-pulse` with `module-role-ducking` | Raw ALSA only; the Pi has **just `alsa-utils`** — no PipeWire, no Python, no pipx |
| TTS playback is a swappable `paplay --property=media.role=phone` command | `aplay -D plughw:CARD=…` opening the **hardware device exclusively**, inside a compiled binary's single-owner playback pump |
| `wyoming-satellite` event hooks (`detection`/`transcript`/`played`) duck the music client | No external hook system — the satellite runs its own Rust state machine and publishes state on a tokio watch channel |

Three concrete blockers follow: (1) the satellite's pump exclusively owns `plughw`, so `snapclient` cannot also open the same device — sharing requires a software mixer; (2) there is no role-ducking to make music duck under speech; (3) the listen-window duck must be driven by the satellite's own state, not an external hook.

## Chosen approach: ALSA `dmix` + `softvol`, ducking driven by the satellite state machine

The lightest mixer that keeps the satellite's "ALSA-only, no daemons, no Python" character is ALSA's own `dmix` (in-process, shared-memory mixing via alsa-lib — no extra service). A `softvol` control on the music branch makes ducking a single ALSA control write that the satellite already has the state signal to drive.

### On-Pi audio topology

```
        ┌──────────────────── Pi: music + voice satellite ─────────────────────┐
hub TTS ─Wyoming─▶ nabu-satellite ─aplay──────────────────▶ duckmix (dmix) ──┐
                       │  arecord ◀──── plughw:CARD=<name>  (CAPTURE, DIRECT, untouched)
                       │  state-watch ─▶ duck task ─amixer─▶ "Music" softvol  │
hub Snap :1704 ─────▶ snapclient ─aplay─▶ music (softvol) ────────────────────┘
                       └──────────────────────────▶ plughw:CARD=<name> (PLAYBACK, shared)
```

- **`duckmix`** — a `dmix` PCM (fronted by `plug` for rate/format conversion) bound to the Jabra **by card name**; the single shared hardware-playback mixer.
- **`music`** — a `softvol`-wrapped PCM feeding `duckmix`; `snapclient` plays here using its **native ALSA backend**, so Snapcast's multi-room latency compensation is unchanged (the hard part is not reimplemented).
- The satellite's **TTS and cues play to `duckmix` directly** (priority, full volume).
- The satellite's **`arecord` stays on direct `plughw:CARD=<name>`** — capture is never contended (`snapclient` is playback-only), so the **wake-tone path is byte-for-byte untouched**.
- `asound.conf` defines **only the named PCMs** (`duckmix`, `music`). It deliberately does **not** define `pcm.!default`, so the satellite's explicit capture device is never hijacked — preserving the 2026-06-26 capture fix (the earlier broken `/etc/asound.conf` was a stale 48 kHz `default` plug device; this one is named-PCM-only).

### How each 2026-06-26 fix is preserved

| Fix | Preserved because |
|-----|-------------------|
| By-name USB card detection (`plughw:CARD=<name>`) | `dmix`/`softvol` slaves bind to the **same** detected card name; no index pinning reintroduced |
| Mic-ADC wake-tone (`--wake-playback-ms`) | Capture-side; `arecord` device unchanged (direct `plughw`) |
| Keep-warm DAC handling (`--keep-warm`) | The satellite still holds one continuous playback stream; `dmix` keeps the hardware open for that stream's lifetime, so the DAC never cold-starts after boot (plausibly *cleaner*; revalidated on-device) |
| USB-autosuspend-off udev rule, performance governor, `Nice=-10` | Unrelated; untouched |
| Mic 16 kHz-native, no resampling | Capture path untouched |

The **only** playback behaviour that genuinely changes is the satellite's `aplay` target (`plughw` → `duckmix`). Its onset/latency tuning (`--start-delay=100000 -F 50000`) now interacts with the `dmix` period/buffer config and must be **listen-tested on-device before `snapclient` is added** (Phase 2 gate).

## Satellite Rust change (small, isolated, mirrors the LED render task)

The change is additive and modelled directly on the existing per-connection LED render task, which already consumes the state watch channel.

- **New CLI flag `--music-mixer <control>`** (in `src/config.rs`), plus optional `--duck-percent <n>` (default `20`). When the flag is **absent**, behaviour is unchanged — reSpeaker and non-music units never opt in (same per-variant opt-in pattern as `--keep-warm` / `--led-spi`).
- **Duck task**: when `--music-mixer` is set, a per-connection task subscribes to the **existing `LedState` watch channel** and applies one rule, structurally identical to the LED's "Idle→off, else→on":
  - `Idle` → set `Music` softvol to **100%**
  - any active state (`Listening`, `Thinking`, `Speaking`) → set `Music` softvol to **`--duck-percent`** (default 20%)
  - This single rule covers both the **listen-window duck** (`Listening`, mic open — stops music bleeding into capture) and the **speech duck** (`Speaking`), with no flapping and no external event hooks.
- **Fail-safe restore**: set the control to 100% at satellite startup and on duck-task teardown (e.g. hub disconnect), so music can never get stuck ducked when no turn is active.
- **Mechanism**: the task applies the level via `amixer sset <control> <pct>%`, matching the satellite's existing "spawn an audio subprocess" pattern (`alsa-utils` ships `amixer`). It is placed behind a `MusicDucker` trait so the **state→level mapping is unit-testable** (real impl shells out to `amixer`; test impl records calls).

Nothing in the hub-side `McpChannelVoice` audio path changes (the 2026-06-06 spec's invariant, retained).

## Provisioning + infra deltas

### `scripts/provision-satellite-rs.sh`

- Add `snapclient` to the existing apt line (currently `alsa-utils` only).
- New script inputs: the **hub host** (for `snapclient` to dial `:1704`) and the **room / Snapcast host-id** (so MA names the player per room; must match the HA area slug). Passed as new positional/optional args or env, consistent with the script's existing `MIC` env-prefix style. These are **host/Pi-side provisioning inputs, not agent config** — no compose/`.env`/`appsettings` entry is required for them.
- After by-name card detection, **write `/etc/asound.conf`** defining `duckmix` (`plug`→`dmix`, slave = `CARD=<name>`) and `music` (`plug`→`softvol`→`duckmix`, exposing the `Music` control). **No `pcm.!default`** — capture stays explicit. (Replaces the script's current unconditional `rm -f /etc/asound.conf` for music units; non-music units keep the removal.)
- Template the unit so **`--mic-command` keeps `plughw:CARD=<name>,DEV=0`** while **`--snd-command` targets `duckmix`** (the current single `s#plughw:0,0#$dev#g` rewrite splits into per-command targets), and add **`--music-mixer Music`** to `ExecStart` for music units.
- Install a **`snapclient` systemd unit**: `--hostID <room>`, dial `<hub-host>:1704`, ALSA output to the `music` PCM; enable + restart (same re-provisioning idempotence as the satellite unit).
- The reSpeaker / non-music path is unaffected (no `--music-mixer`, no `asound.conf`, no `snapclient`).

### Repo infra

- **`DockerCompose/docker-compose.yml`**: new `music-assistant` service (`ghcr.io/music-assistant/server:latest`, `music-assistant-data:/data` volume, ports `8095/1704/1705/1780`, `restart: unless-stopped`, MA built-in Snapcast server). **No new agent env var, no `.env` secret, no `appsettings` key** — Spotify auth lives in MA's own data volume (OAuth), documented not wired.
- **`CLAUDE.md`**: add `music-assistant` to the launch command(s); add a short music-satellite/`dmix` note under the Voice Satellite section.
- **`Domain/Prompts/HomeAssistantPrompt.cs`** (in scope — cheap in-repo polish): short guidance on MA's `music_assistant.play_media` (search-by-name) and `media_player.join`/`unjoin` grouping, defaulting to the speaking room. TDD with a prompt-content unit test mirroring existing prompt tests.
- **`Dashboard.Client` `HealthGrid`** (in scope — cheap in-repo polish): a `music-assistant` health tile. Exact wiring (existing heartbeat vs a small `:8095` probe) is decided during the slice by reading the current `HealthGrid` implementation; **no new metric DTOs, query-service methods, or dashboard pages**.

### Manual steps (you, in the UIs — cannot be scripted)

- Add the HA **Music Assistant** integration → `http://music-assistant:8095`.
- In MA: add the **Spotify (Premium OAuth)** provider and the **Snapcast** player provider (built-in server); add radio/local providers as desired.
- **Name each player to its room slug** and assign it to the matching HA area, so `/ha/areas/<room>/` resolves the right `media_player` (the load-bearing convention from the 2026-06-06 spec).

## Data flow, identity, grouping, error handling

Unchanged from the 2026-06-06 spec and incorporated by reference: voice-initiated and HA-initiated playback, room→player resolution via HA areas, `media_player.join`/`unjoin` grouping, and the failure-mode table. The **only** delta is the coexistence row: "speech while music plays" and "music bleeding into mic during capture" are now handled by the **satellite-state-driven `softvol` duck** (this spec's §"Chosen approach") instead of PipeWire role-ducking + wyoming event hooks.

## Testing

Infra-first, so automated coverage is small and concentrated; correctness is demonstrated end-to-end on-device.

### Unit tests (TDD, RED first)
- **Satellite**: the duck-level mapping (`Idle`→100, active→`duck-percent`) and that the `MusicDucker` is invoked on each state transition (trait-mocked). Plus startup/teardown fail-safe restore to 100%.
- **`HomeAssistantPrompt`**: prompt-content test asserting the MA `play_media` / `join` idiom text is present.

### On-device E2E (manual, scripted) — gates per phase
1. From MA UI, play a Spotify track to one Pi `snapclient`; confirm audio.
2. **Phase-2 gate**: with `dmix`/`softvol` installed and the satellite's `aplay` on `duckmix`, confirm **TTS onset, cue beeps, and keep-warm sound identical to pre-change** *before* trusting the mixer; then confirm music + TTS mix without an exclusive-device error.
3. Speak while music plays; confirm music **ducks** (`Speaking`) and **restores** (`Idle`); wake while music plays; confirm wake/STT still work (listen-window duck on `Listening`).
4. (Deferred, needs ≥2 Pis) `media_player.join`/`unjoin`: confirm synced playback and that speaking ducks only the addressed room.

## Phasing (each slice ends in a clean commit + passing suite)

1. **MA infra**: `music-assistant` compose service + `CLAUDE.md` launch command; HA integration + Spotify/Snapcast providers (manual); play a track to one `snapclient` (default ALSA, pre-`dmix`). *Done when:* a track plays on one satellite, controllable from HA.
2. **dmix/softvol on the Pi**: provisioning writes `asound.conf`, points the satellite's `aplay` at `duckmix` and `snapclient` at `music`. *Done when:* the Phase-2 gate passes — TTS/keep-warm unchanged, and music + TTS coexist.
3. **Satellite ducking**: `--music-mixer` flag + duck task + TDD. *Done when:* music ducks under speech and during the listen window, restoring on idle.
4. **In-repo polish**: `HomeAssistantPrompt` voice-control guidance (+ test) and the `HealthGrid` `music-assistant` tile. *Done when:* "play X here / in <room>", "pause", "next", "louder" read clearly in the prompt and the tile reports MA health.
5. **(Deferred — needs a 2nd Pi)** multi-room `join`/`unjoin` sync validation + buffer tuning.

## Out of scope (this spec)

Same as the 2026-06-06 spec, plus: PipeWire/role-ducking (rejected here), and migrating the satellite to LVA/ESPHome (decision record in the 2026-06-06 spec stands).

## Risks (validated on-device, not blockers)

- **`dmix` onset/buffer vs the current `--start-delay=100000 -F 50000` tuning** — the one change that genuinely needs a listen-test (Phase-2 gate).
- **MA built-in Snapcast stream format** — whether it can serve **22050 mono** (the Jabra's natural ceiling) so `dmix` skips resampling; otherwise the `dmix` slave runs at a hw-native rate (e.g. 48 kHz) and `plug` resamples — still correct, marginally more CPU.
- **Keep-warm under `dmix`** — expected preserved/cleaner, but confirmed by the Phase-2 listen-test.
- **Multi-room sync** — Snapcast's hardest feature; mitigated by keeping `snapclient`'s native ALSA backend (its latency compensation intact) and validated only when a 2nd Pi exists.

## Style and layering rules to honour during implementation

- This spec intentionally introduces **no new agent environment variable or config key**. If a future variant (e.g. a dedicated `snapserver`) needs one, it must be added to `docker-compose.yml`, `.env` (secrets only), `appsettings.json`, and `appsettings.Development.json` in the same change, per repo policy.
- Satellite Rust: follow the crate's existing patterns (`src/config.rs` flags, per-connection tasks on the state watch channel, subprocess-driven audio); the `MusicDucker` trait keeps the mapping unit-testable. TDD RED→GREEN.
- `HomeAssistantPrompt` change: file-scoped namespace, no XML doc comments, comments only for non-obvious "why", matching prompt-content unit test. No trailing newline in any `.cs` file.
- Keep `McpChannelVoice`'s audio path and the satellite's **capture** path untouched — the design depends on both.
