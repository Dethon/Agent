# Satellite Media Player (Spotify via Home Assistant) — Design Spec

**Date**: 2026-06-06
**Status**: Approved for planning
**Owner**: Francisco Crespo
**Supersedes (hardware only)**: the Pi Zero 2 W satellite-hardware choice in `2026-05-08-voice-satellites-design.md`. That spec listed "Music/media playback features" as out of scope; this spec adds them.

## Goal

Turn each voice satellite into a real, Home-Assistant-controllable **media player** so music — Spotify, web radio, podcasts, local files — can play through the satellite's own speaker, with synchronized multi-room playback when rooms are grouped. A spoken request ("play some jazz in the kitchen") drives playback through the agent's existing `/ha` virtual filesystem; an external HA automation can equally start playback. Music ducks automatically whenever the satellite needs to speak (a voice reply or an announcement), and never the other way around.

This is deliberately built so the **voice/TTS path in `McpChannelVoice` does not change**. The original spec made the voice channel the single owner of each satellite's audio to avoid a "two-master" problem. We preserve that: speech and music are not two masters of one stream — they are **two streams with a fixed priority (speech > music), enforced by the Pi's audio mixer**. The voice channel still exclusively owns the TTS stream; music is a separate, lower-priority stream that ducks under it.

## Constraints and choices

- **Media brain**: [Music Assistant](https://music-assistant.io/) (MA). MA is the library/streaming layer; streaming services (Spotify, radio, podcasts, local files) are MA *providers*. Spotify needs a **Premium** account, authenticated once in MA's own UI (server-side librespot) — the Pi never runs Spotify Connect, and no Spotify secret is wired through this repo.
- **Multi-room transport**: [Snapcast](https://github.com/badaix/snapcast). Each satellite runs `snapclient`; MA's Snapcast player provider exposes each client as an MA player, which the HA Music Assistant integration surfaces as a `media_player.<room>` entity. Rooms are independent by default and can be grouped for perfectly synced whole-house audio.
- **Speech/music coexistence**: **duck under speech**, enforced **on-device** by the Pi's audio mixer (PipeWire / `pipewire-pulse` role-ducking). No network round-trip, no coordination code in `McpChannelVoice`.
- **Repo footprint**: **infra-first**. The agent controls music through the **existing** `/ha` VFS (`media_player.*` and MA's `music_assistant.*` actions). The only optional code change is prompt guidance.
- **Hardware**: standardize the satellite fleet on **Raspberry Pi 4 Model B (2 GB)**. Reasoning in [Hardware](#hardware).
- **HA runtime**: this stack runs Home Assistant **Container** (`homeassistant` compose service), which has **no add-on store**. Music Assistant therefore runs as its **own container**, not an HA add-on, and the HA Music Assistant integration connects to it over the compose network.
- **Build vs. borrow**: maximise reuse. MA, Snapcast, and the HA Music Assistant integration are all stock. The only things we author are compose/service wiring, the Pi provisioning steps, and (optionally) a short prompt addition.

## Hardware

The combined role — **always-on wake detection running concurrently with an always-on music stream** — changes the hardware calculus from the voice-only original spec.

The Pi Zero 2 W (quad A53 @ 1 GHz, 512 MB, single-band 2.4 GHz Wi-Fi, micro-USB power) is adequate on CPU/RAM for the added `snapclient` + mixer, but its real weaknesses for *this* role are:

1. **Single-band 2.4 GHz Wi-Fi** — Snapcast multi-room sync is jitter-sensitive, and it would now share one congested band with the Wyoming audio path. Primary reliability risk: sync drift and dropouts.
2. **Power budget** — micro-USB ≤500 mA plus an active speaker/amp can brown out under load.
3. **Echo headroom** — music out of the speaker bleeding into the mic stresses wake/STT; little AEC headroom on this hardware.

The fleet standardizes on **Raspberry Pi 4 Model B (2 GB)**: dual-band Wi-Fi + Gigabit Ethernet (removes the sync risk), an A72 quad core and 2 GB RAM (ample headroom for future on-device wake words / AEC), USB 3, powered by an official USB-C 5 V/3 A PSU. At this light, steady load a passive heatsink is sufficient. Standardizing (rather than a mixed fleet) keeps provisioning, imaging, and the systemd units uniform.

> The software stack below is board-agnostic; nothing depends on the Pi 4 specifically, so a future board swap is a provisioning concern only.

## Architecture

```
Pi 4 (×N)                                Hub / Docker host                      Home Assistant
┌──────────────────────────────┐         ┌────────────────────────────┐        ┌─────────────────────┐
│ wyoming-satellite ──┐        │ Wyoming  │  McpChannelVoice (.NET)     │◀─MCP──▶│ Agent (mycroft)     │
│  (mic, wake, TTS    │        │◀────────▶│  *** audio path unchanged ***│        └─────────────────────┘
│   playback) role=phone│      │          └────────────────────────────┘        ┌─────────────────────┐
│        ▼              │      │                                                  │ Home Assistant       │
│   PipeWire / pipewire-pulse  │          ┌────────────────────────────┐  /ha    │  (Container)         │
│   module-role-ducking:       │          │  music-assistant (container)│ ◀─VFS──▶│  + Music Assistant   │
│   phone/notification ducks   │          │   ├ Spotify / radio / local │  tools  │    integration       │
│   music                      │          │   ├ providers              │◀───────▶│  media_player.<room> │
│        ▲              │      │   Snap   │   └ built-in Snapcast server│         │   (per HA area)      │
│ snapclient role=music │──────┼─────────▶│      :1704 / :1705 / :1780  │         └─────────────────────┘
└──────────────────────────────┘ stream   └────────────────────────────┘
```

Hub and agent host may be the same machine or two LAN boxes, exactly as today. Music Assistant joins the existing compose network and reaches HA (and vice versa) by service name. Snapclients on the Pis connect to the hub's Snapcast stream port.

## Components

### Stock components (no code we own)

| Component | Source | Role |
|-----------|--------|------|
| `music-assistant` | `ghcr.io/music-assistant/server` (container) | Library + streaming providers (Spotify/radio/podcasts/local); Snapcast player provider; **built-in Snapcast server** |
| Snapcast | bundled in MA (built-in server) + `snapclient` apt package on each Pi | Synchronized multi-room transport |
| HA **Music Assistant** integration | Home Assistant core integration (config entry → MA server URL) | Surfaces each MA/Snapcast player as a `media_player.<room>` entity; provides `music_assistant.*` services |
| HA **Spotify** account | configured inside MA (OAuth, Premium) | Spotify as an MA provider; decoded server-side, no per-Pi Spotify Connect |
| `snapclient` | apt on Pi OS Lite (Bookworm) | Plays the synced stream through PipeWire with `media.role=music` |
| `wyoming-satellite` + `wyoming-openwakeword` | existing (unchanged) | Mic, wake word, TTS playback (now tagged `media.role=phone`) |

> **Snapcast server placement.** Primary choice: Music Assistant's **built-in Snapcast server** (one fewer service, MA owns the stream pipeline end-to-end). Publish ports `1704` (stream), `1705` (control), `1780` (web) from the `music-assistant` container. Alternative, if independent health/scaling is wanted later: a dedicated `snapserver` container with MA's Snapcast provider pointed at it. The Pi-side `snapclient` config is identical either way.

### New components (this project owns)

**No new .NET project, no new C# service.** The repo deltas are infra + an optional prompt string:

| Area | Change |
|------|--------|
| `DockerCompose/docker-compose.yml` | New `music-assistant` service (volume, ports, network); add it to the launch command in `CLAUDE.md` |
| `appsettings*.json` / `.env` | **No new agent env vars or secrets.** Spotify auth lives in MA's own data volume (OAuth). Documented, not wired. |
| `scripts/provision-satellite.sh` | Pi 4 image + existing wyoming stack + `snapclient` + PipeWire role-ducking + wyoming-satellite event hooks (see [Provisioning](#satellite-provisioning)) |
| `Domain/Prompts/HomeAssistantPrompt.cs` (optional) | Short guidance on MA's `music_assistant.play_media` (search-by-name) and `media_player.join`/`unjoin` for grouping |

### Why no message-pipeline change is needed

The agent already receives the originating room and satellite id with every voice utterance. `OpenRouterChatClient` renders the user message as `"Message from {sender} (in {room} via {satelliteId}):"`; `Location` (room) and `SatelliteId` ride the chain `TranscriptDispatcher → ChannelNotificationEmitter → ChannelMessageNotification → McpChannelConnection → ChannelMessage → ChatMonitor → ChatMessage` (commit `b98e1730`, `Domain/Extensions/ChatMessageExtensions.cs`). So "play music" from the kitchen already arrives tagged with the kitchen, and the agent can target that room's `media_player` with no code change.

## Data flow

### Music playback (voice-initiated)

1. **Speak.** User: "play some jazz" to the kitchen satellite. Wake → STT → transcript, exactly as today.
2. **Dispatch.** The transcript reaches `mycroft` tagged `(in Kitchen via kitchen-01)` — no change to the voice path.
3. **Resolve room → player.** The agent lists `/ha/areas/kitchen/` and finds `media_player.kitchen` (the MA/Snapcast player assigned to the Kitchen HA area).
4. **Play.** The agent runs the player's action via the existing VFS: `fs_exec "play_media.sh --media_content_id 'jazz' --media_content_type music"` (or the richer `music_assistant.play_media` action with a name search). MA resolves the request against the Spotify provider and streams to the Snapcast server.
5. **Audio.** Snapserver streams to the kitchen `snapclient`, which plays through PipeWire at `media.role=music`.

### Music playback (HA-initiated)

Identical from step 4 on, but the trigger is an HA automation calling the same `media_player`/`music_assistant` service. No agent involvement.

### Synced multi-room

The agent (or an HA automation) groups rooms with `media_player.join` (target = `media_player.kitchen`, members = `media_player.living_room`, …) and then plays once; Snapcast keeps the grouped clients in sync. `media_player.unjoin` splits them again.

### Speech ducking under music (the core mechanism)

- `snapclient` outputs its stream with `media.role=music`.
- `wyoming-satellite`'s TTS playback runs through `paplay --property=media.role=phone` (the existing audio path on the hub side is unchanged; only the **on-Pi playback command** carries the role tag).
- PipeWire's `pipewire-pulse` loads `module-role-ducking` with `trigger_roles=phone,notification ducking_roles=music` (target ~20%). Whenever a reply or announce plays, music on **that satellite only** ducks and restores automatically; grouped peers keep playing at full volume. No coordination crosses the network.

### Listen-window ducking (echo mitigation)

`module-role-ducking` only fires while a speech stream is *playing*. During the **capture** window there is no playback, so music would otherwise bleed into the mic. `wyoming-satellite` **event hooks** close the gap: on `detection` (wake) → duck/mute the local `snapclient` stream; on `transcript`/`played` → restore. This stays entirely on-device.

## Configuration

### Docker Compose (skeleton)

```yaml
music-assistant:
  image: ghcr.io/music-assistant/server:latest
  volumes: [ "music-assistant-data:/data" ]
  ports:
    - "8095:8095"     # web UI + API/websocket (HA integration target)
    - "1704:1704"     # snapcast stream  (snapclients connect here)
    - "1705:1705"     # snapcast control
    - "1780:1780"     # snapcast web
  restart: unless-stopped
  # Bridge networking is sufficient for a Snapcast-only setup (clients are
  # configured with the hub host). Switch to host networking only if mDNS-
  # discovered players (Chromecast/Sonos/AirPlay) are added later.
```

The HA container reaches MA at `http://music-assistant:8095`; satellites' `snapclient` connect to `<hub-host>:1704`. The launch command in `CLAUDE.md` gains `music-assistant`.

### Home Assistant

- Add the **Music Assistant** integration (config entry → `http://music-assistant:8095`). It creates one `media_player` per MA/Snapcast player.
- **Naming/area convention (load-bearing):** name each player after its room and assign it to the HA **area** whose slug matches the satellite's configured `Room` (e.g. satellite `Room: "Kitchen"` ↔ HA area `kitchen` ↔ `media_player.kitchen`). This is what makes `/ha/areas/<room>/` list the right player so the agent can target "this room" with no mapping table.
- In MA: add the **Spotify** provider (Premium OAuth) and the **Snapcast** player provider (built-in server). Add radio/local providers as desired.

### Satellite provisioning

Extend `scripts/provision-satellite.sh` (now targeting **Pi 4 / Pi OS Lite 64-bit Bookworm**):

1. Flash Pi OS Lite 64-bit; enable Wi-Fi/SSH; official USB-C 3 A PSU.
2. Existing: `pipx install wyoming-satellite wyoming-openwakeword`; system audio deps.
3. **New:** `apt install snapclient`; systemd unit pointing at `<hub-host>:1704`, output via PipeWire with `media.role=music`, `--hostID <satelliteId>` so the Snapcast client name matches the satellite.
4. **New:** ensure PipeWire + `pipewire-pulse`; load `module-role-ducking` (`trigger_roles=phone,notification ducking_roles=music`, ~20% target). If a given image lacks the module, fall back to a WirePlumber ducking rule — decided per image during the slice.
5. **New:** set `wyoming-satellite` playback command to `paplay --property=media.role=phone`; add `detection`/`transcript`(or `played`) event hooks that duck/restore the local `snapclient` for the listen window.
6. Reboot. Upgrades remain `pipx upgrade` + `apt upgrade`.

## Identity, rooms, and grouping

- Satellite identity/threading is unchanged (per the original spec): each satellite is its own conversation thread; `sender` is the configured identity.
- **Room ↔ media_player** is resolved through HA areas (above), not a new mapping. The agent already knows the speaking room from the rendered message prefix.
- **Grouping** is HA-native (`media_player.join`/`unjoin`) over the MA/Snapcast players.

## Observability

Music playback does **not** flow through `McpChannelVoice`, so no new voice metric enums are required. Coverage comes from existing paths:

- **Tool analytics** already count the agent's `media_player.*` / `music_assistant.*` calls through the HA VFS tool path — no change.
- **`HealthGrid.razor`** gains a `music-assistant` tile. The simplest signal is a periodic health probe of `http://music-assistant:8095` (and, if a dedicated `snapserver` is later used, its control port). Whether this rides the existing heartbeat mechanism or a small probe is decided during the slice by reading the current HealthGrid wiring.

No dashboard pages, metric DTOs, or query-service methods are added.

## Error handling

| Failure | Behaviour |
|---------|-----------|
| MA / Spotify provider down | Player action returns an HA/MA error through the VFS (`fs_exec` non-zero exit); the agent reports it. No retry queue. |
| Spotify auth expired | MA marks the provider unauthenticated; play actions fail with a clear error; re-OAuth in MA UI. Surfaced to the user via the agent's normal error reply. |
| `snapclient` offline / Pi down | That room's `media_player` shows unavailable in HA; grouped peers keep playing. |
| Snapcast sync drift / jitter | Mitigated by Pi 4 dual-band/Ethernet + Snapcast buffer tuning; isolated to infra. |
| Speech while music plays | `module-role-ducking` ducks music for that satellite and restores after; grouped peers unaffected. (Replaces any need for channel coordination.) |
| Music bleeding into mic during capture | `wyoming-satellite` event-hook duck for the listen window; ReSpeaker HAT for better SNR. |
| `module-role-ducking` unavailable in image | WirePlumber ducking-rule fallback; chosen during the ducking slice. |
| High-priority announce during music | Announce plays as a `phone`-role stream → music ducks automatically; existing reply-preemption behaviour is unchanged. |

## Testing

This is infra-first, so automated coverage is small and concentrated; correctness is mostly demonstrated end-to-end.

### Unit tests
- If the optional `HomeAssistantPrompt` guidance is added: a prompt-content test asserting the MA `play_media` / `join` idiom text is present (mirrors existing prompt tests). No other unit tests — there is no new .NET behaviour.

### Integration tests
- None required for the music path (it does not traverse `McpChannelVoice` or new agent code). The existing HA VFS `fs_exec` tests already cover the action-invocation mechanism the agent uses to drive `media_player.*`.

### E2E (manual, scripted)
1. From MA UI, play a Spotify track to one Pi `snapclient`; confirm audio.
2. Speak to that satellite; confirm music **ducks** during the reply and **restores** after.
3. Group two rooms (`media_player.join`); confirm **synced** playback; speak to one room; confirm only that room ducks.
4. Voice-drive it: say "play <artist> in the kitchen"; confirm the agent targets `media_player.kitchen` and music starts.
5. Wake the satellite **while music plays**; confirm wake/STT still work (listen-window duck) and the reply is intelligible.

## Phasing

Each slice ends in a clean commit and a passing test suite.

### Slice 1 — Music Assistant + Snapcast, one room
- `music-assistant` compose service (+ `CLAUDE.md` launch command); MA built-in Snapcast server.
- HA Music Assistant integration; Spotify + Snapcast providers in MA.
- One Pi 4 running `snapclient`; play Spotify to it from MA/HA.
- **Done when:** a track plays on one satellite, controllable from HA. (Voice may still conflict — ducking lands next.)

### Slice 2 — On-device coexistence (ducking)
- PipeWire role tags (`music` on snapclient, `phone` on TTS playback) + `module-role-ducking`.
- `wyoming-satellite` event-hook duck for the listen window.
- **Done when:** music ducks under a reply/announce and during capture, then restores — no audible conflict.

### Slice 3 — Synced multi-room
- ≥2 Pi 4 satellites; verify `media_player.join`/`unjoin` grouping and Snapcast sync; tune buffers.
- **Done when:** the same track plays in sync across grouped rooms; speaking ducks only the addressed room.

### Slice 4 — Voice control polish
- Optional `HomeAssistantPrompt` guidance for MA `play_media` (search-by-name) and grouping.
- **Done when:** "play <X> here / in <room>", "pause", "next", "louder", and "play <X> everywhere" work reliably by voice, defaulting to the speaking room.

### Slice 5 — Fleet standardization + ops
- Extend `scripts/provision-satellite.sh` for Pi 4 (snapclient + ducking + event hooks); migrate the fleet to Pi 4.
- `HealthGrid` tile for `music-assistant`.
- Update `CLAUDE.md` (launch command, hardware note) and cross-link this spec from the original voice spec.
- **Done when:** a fresh Pi 4 provisions to a fully working voice **and** music satellite from the script alone.

## Out of scope (for this spec)

- Replacing the existing TTS/announce path with MA's built-in announce/TTS (kept separate; ducking already unifies them at the speaker).
- Managing MA providers/playlists through the agent (done in MA's UI).
- Voice-fingerprint-aware personalization ("play *my* playlist").
- Whole-house intercom / satellite-to-satellite audio (already out of scope upstream).
- Battery-powered satellites; bespoke AEC hardware beyond the ReSpeaker HAT.

## Risks

- **Snapcast sync/latency tuning** across rooms — mitigated by Pi 4 dual-band/Ethernet and buffer config; infra-only.
- **Acoustic echo while music plays** (music → mic) — mitigated by listen-window duck + ReSpeaker HAT; AEC remains best-effort.
- **Spotify provider/auth churn** in MA (token expiry, librespot quirks) — surfaced as clear play errors; re-OAuth in MA UI; no agent coupling.
- **`module-role-ducking` availability** on the chosen Pi OS image — WirePlumber rule fallback identified in Slice 2.
- **Fleet migration cost** (Pi 4 across all rooms) — accepted; provisioning is scripted to keep per-unit effort low.

## Style and layering rules to honour during implementation

- Per repo policy, any new environment variable/config key is added to `DockerCompose/docker-compose.yml`, `DockerCompose/.env` (secrets only), `appsettings.json`, and `appsettings.Development.json` **in the same change**. This spec intentionally introduces **none** — if the dedicated-`snapserver` alternative is chosen and needs a host/port key, it must follow this rule.
- If the optional prompt change is made: file-scoped namespaces, no XML doc comments, comments only for non-obvious "why", and a matching prompt-content unit test (TDD per `.claude/rules/tdd.md`).
- Keep `McpChannelVoice`'s audio path untouched — the design depends on it.
