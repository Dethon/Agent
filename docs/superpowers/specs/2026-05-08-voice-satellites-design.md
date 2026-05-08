# Voice Satellites — Design Spec

**Date**: 2026-05-08
**Status**: Approved for planning
**Owner**: Francisco Crespo

## Goal

Add an Alexa-like voice interface to the agent. Small satellite devices in different rooms capture audio after a wake word, send it to a local hub for speech-to-text, dispatch the transcript to the agent through a new MCP channel, then play the agent's spoken reply back through the originating satellite.

## Constraints and choices

- **Satellite hardware**: Raspberry Pi Zero 2 W per room, USB-powered (≤500 mA), USB mic (e.g., ReSpeaker 2-Mic HAT). Approximately 5 satellites at home.
- **Trigger**: on-device wake word + voice activity detection. Always-on listening, audio leaves the device only after the wake word fires.
- **Voice out**: full text-to-speech reply played on the satellite that asked.
- **Identity**: per-satellite static identity. Each satellite is provisioned with a fixed identity (e.g., `kitchen-01 → household`, `bedroom-01 → francisco`). No voice fingerprinting.
- **Wake-word engine**: openWakeWord (Apache-2.0). Custom wake words trainable later.
- **Speech-to-text**: pluggable. Default `wyoming-faster-whisper` running locally; cloud STT (e.g., OpenAI) selectable via configuration with no code change.
- **Text-to-speech**: pluggable. Default `wyoming-piper` running locally; cloud TTS selectable per-satellite later.
- **Multi-user**: occasional. Concurrent utterances from different satellites are independent threads.
- **Hub host**: Pi 5 or mini-PC. May be the same Docker host as the agent or a separate LAN box. The agent connects to the channel via existing MCP HTTP transport. LAN is trusted; no Tailscale or Caddy required.
- **Build vs. borrow**: maximise reuse of the Wyoming ecosystem (`wyoming-satellite`, `wyoming-faster-whisper`, `wyoming-piper`, `wyoming-openwakeword`). The only new code is the bridge between Wyoming and the agent's MCP channel protocol.

## Architecture

```
Pi Zero 2 W (×N)                Hub (Pi 5 / mini-PC)            Agent host
┌────────────────────┐  Wyoming  ┌──────────────────────────┐   ┌──────────┐
│ wyoming-satellite  │◀─────────▶│  McpChannelVoice (.NET)  │   │  Agent   │
│  ├ openWakeWord    │           │   ├ Wyoming server       │◀─▶│ existing │
│  ├ silero-VAD      │           │   ├ ISpeechToText        │MCP│ pipeline │
│  ├ mic capture     │           │   ├ ITextToSpeech        │HTTP│         │
│  └ playback        │           │   ├ SatelliteRegistry    │   └──────────┘
└────────────────────┘           │   └ MCP HTTP transport   │
                                 ├──────────────────────────┤
                                 │  wyoming-faster-whisper  │ (Wyoming over TCP)
                                 │  wyoming-piper           │
                                 └──────────────────────────┘
```

The hub and agent host may be the same machine or two boxes on the same LAN. The connection between `McpChannelVoice` and the agent uses the same MCP HTTP transport that `McpChannelTelegram` and `McpChannelSignalR` already use.

## Components

### Stock components (no code we own)

| Component | Source | Role |
|-----------|--------|------|
| `wyoming-satellite` | Rhasspy project, installed via `pipx` on Pi OS Lite | Mic capture, on-device openWakeWord, VAD, audio I/O, optional LED/button events |
| `wyoming-faster-whisper` | `rhasspy/wyoming-whisper` Docker image | STT, model selectable from `tiny` through `large-v3` and `distil-large-v3` |
| `wyoming-piper` | `rhasspy/wyoming-piper` Docker image | TTS, voice selectable per language |

### New components (this project owns)

#### `McpChannelVoice` — new project

Mirrors the layout of `McpChannelTelegram` and `McpChannelSignalR`:

```
McpChannelVoice/
├── Program.cs
├── McpChannelVoice.csproj
├── Dockerfile
├── appsettings.json
├── appsettings.Development.json
├── McpTools/
│   ├── SendReplyTool.cs
│   └── RequestApprovalTool.cs
├── Modules/
│   └── VoiceModule.cs
├── Services/
│   ├── WyomingServer.cs              // Wyoming inbound from satellites
│   ├── SatelliteSession.cs           // one active wake-to-reply session
│   ├── SatelliteRegistry.cs          // id → identity, room, overrides
│   ├── ApprovalGrammarParser.cs      // yes/no/sí/no parsing
│   ├── ChannelNotificationEmitter.cs // builds channel/message notifications
│   └── VoiceMetricsPublisher.cs      // wraps IMetricsPublisher
└── Settings/
    └── VoiceSettings.cs
```

#### Speech contracts — new files in `Domain/Contracts/`

```csharp
public interface ISpeechToText
{
    Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        TranscriptionOptions options,
        CancellationToken ct);
}

public interface ITextToSpeech
{
    IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SynthesisOptions options,
        CancellationToken ct);
}
```

DTOs (`AudioChunk`, `TranscriptionResult`, `TranscriptionOptions`, `SynthesisOptions`) live in `Domain/DTOs/Voice/`.

#### Speech adapters — new files in `Infrastructure/Clients/Voice/`

Initial adapters:

| Class | Backend | When |
|-------|---------|------|
| `WyomingSpeechToText` | `wyoming-faster-whisper` over TCP | Default, all hub sizes |
| `WyomingTextToSpeech` | `wyoming-piper` over TCP | Default |
| `OpenAiSpeechToText` | OpenAI `audio/transcriptions` | Slice 5; cloud fallback |
| `OpenAiTextToSpeech` | OpenAI `audio/speech` | Slice 5; per-satellite quality bump |

DI registration in `McpChannelVoice/Modules/VoiceModule.cs` switches by configuration.

## Data flow

### Utterance round-trip

1. **Wake**. Satellite's openWakeWord fires. `wyoming-satellite` opens a Wyoming session to `McpChannelVoice` and sends an `info` event including the satellite id.
2. **Capture**. Satellite streams 16 kHz / 16-bit mono PCM frames; silero-VAD ends the segment on roughly 700 ms of silence.
3. **STT**. `McpChannelVoice` forwards the audio stream to the configured `ISpeechToText`. Default → Wyoming client to `wyoming-faster-whisper`. Returns transcript, language, and confidence.
4. **Confidence gate**. Empty transcripts and transcripts below a configurable confidence threshold are dropped before reaching the agent. The satellite plays a soft dismiss tone.
5. **Dispatch**. `McpChannelVoice` resolves satellite id → identity, room via `SatelliteRegistry`. Builds a `channel/message` notification (`text` = transcript, `sender` = mapped identity, `channelId` = satellite id, metadata includes `room`). Pushes to the agent over MCP HTTP.
6. **Agent processes**. Existing pipeline runs unchanged: thread persistence, memory recall hook, LLM, tool calls, `send_reply` invocations.
7. **Reply**. Agent calls the channel's `send_reply` tool with the response text. `McpChannelVoice` routes to the originating `SatelliteSession`, runs `ITextToSpeech` (default → `wyoming-piper`), streams audio frames back through the still-open Wyoming session.
8. **Playback**. Satellite plays audio; LED returns to idle. Session closes after the last audio frame.

### Approval flow (`request_approval`)

The channel speaks the prompt via TTS, then opens a fresh capture window. STT result is parsed against an `ApprovalGrammarParser` covering `yes/no/sí/no/cancel/confirm/ok` plus close synonyms. Behaviour:

- High-confidence positive → confirm.
- High-confidence negative → decline.
- Low confidence or unrecognized → re-prompt once. On second failure, **decline by default**.
- If the satellite has a hardware button (ReSpeaker HAT supports this), `wyoming-satellite` forwards the press as a Wyoming event; the channel maps press = confirm, double-press = decline. Optional, used as fallback in noisy rooms.

## Configuration

### `McpChannelVoice/appsettings.json` (skeleton)

```json
{
  "Voice": {
    "WyomingServer": { "Host": "0.0.0.0", "Port": 10700 },
    "Stt": {
      "Provider": "Wyoming",
      "Wyoming": { "Host": "wyoming-whisper", "Port": 10300, "Model": "base" },
      "OpenAi":  { "Model": "whisper-1" }
    },
    "Tts": {
      "Provider": "Wyoming",
      "Wyoming": { "Host": "wyoming-piper", "Port": 10200, "Voice": "es_ES-davefx-medium" },
      "OpenAi":  { "Model": "tts-1", "Voice": "alloy" }
    },
    "ConfidenceThreshold": 0.4,
    "Satellites": {
      "kitchen-01":     { "Identity": "household", "Room": "Kitchen",     "WakeWord": "hey_jarvis" },
      "living-room-01": { "Identity": "household", "Room": "Living Room", "WakeWord": "hey_jarvis" },
      "bedroom-01":     { "Identity": "francisco", "Room": "Bedroom",     "WakeWord": "hey_jarvis" }
    }
  }
}
```

Per-satellite overrides for STT/TTS provider live under `Voice.Satellites.<id>.Stt` / `.Tts` and overlay the defaults.

### Docker Compose

New services in `DockerCompose/docker-compose.yml`:

```yaml
mcp-channel-voice:
  build: { context: .., dockerfile: McpChannelVoice/Dockerfile }
  environment:
    - ASPNETCORE_URLS=http://+:5010
    - Voice__Stt__Wyoming__Host=wyoming-whisper
    - Voice__Tts__Wyoming__Host=wyoming-piper
    - OPENAI_API_KEY=${OPENAI_API_KEY}
  ports: [ "10700:10700", "5010:5010" ]
  depends_on: [ wyoming-whisper, wyoming-piper ]

wyoming-whisper:
  image: rhasspy/wyoming-whisper:latest
  command: --model base --language es --device cpu
  volumes: [ "whisper-data:/data" ]

wyoming-piper:
  image: rhasspy/wyoming-piper:latest
  command: --voice es_ES-davefx-medium
  volumes: [ "piper-data:/data" ]
```

The agent's `ChannelEndpoints` configuration adds `mcp-channel-voice:5010`.

`OPENAI_API_KEY` is added as a placeholder to `DockerCompose/.env` and surfaces in `appsettings.Development.json` only when an OpenAI provider is selected. Per repo policy, all infrastructure files are updated in the same change.

### Satellite provisioning

A one-time script (`scripts/provision-satellite.sh`) on each Pi Zero 2 W:

1. Flash Pi OS Lite 64-bit, enable Wi-Fi and SSH.
2. `apt install` system audio dependencies; `pipx install wyoming-satellite wyoming-openwakeword`.
3. Install a systemd unit parameterised with: satellite id, hub address, wake word, mic device, optional button GPIO.
4. Reboot.

No bespoke firmware. Upgrades are `pipx upgrade` plus `apt upgrade`.

## Identity and threading

Each satellite is its own conversation thread, keyed by satellite id. Within a thread, the `sender` field is the configured identity. This matches the existing channel model (Telegram chat = thread, WebChat avatar = sender). Memory and recall key off the identity exactly as today.

Unknown satellite ids are rejected at the Wyoming `info` handshake. Optional Wyoming PSK auth is supported via configuration; off by default since the LAN is trusted.

## Observability

`McpChannelVoice` publishes `MetricEvent`s through the existing `IMetricsPublisher`. New enum values:

```csharp
// Domain/DTOs/Metrics/Enums/VoiceDimension.cs
public enum VoiceDimension
{
    SatelliteId, Room, Identity, WakeWord, Language,
    SttProvider, SttModel, TtsProvider, TtsVoice, Outcome
}

// Domain/DTOs/Metrics/Enums/VoiceMetric.cs
public enum VoiceMetric
{
    WakeTriggered, UtteranceTranscribed, AudioSeconds,
    SttLatencyMs, TtsLatencyMs, WakeToFirstAudioMs,
    ApprovalResolved, SttError, TtsError
}
```

Cloud STT/TTS adapters publish their cost via the existing `TokenMetric` path tagged with a new `Origin = "voice"` dimension value, so they slice into existing token breakdowns without UI changes.

## Dashboard changes

### New page — `Dashboard.Client/Pages/Voice.razor`

Mirrors `Tools.razor`. Default views:

- KPIs: utterances (24h), median wake-to-first-audio (24h), STT errors (24h), TTS errors (24h).
- Charts (`DynamicChart` + `PillSelector`): utterances by room, wake-to-first-audio latency by satellite, STT errors by provider+model, approval outcomes.

A nav entry is added to the dashboard's main navigation component.

### Existing pages

- **`Overview.razor`**: two new `KpiCard`s — "Utterances (24h)" and "Median voice latency (24h)".
- **`Errors.razor`**: voice errors appear as a new error source. If the page already aggregates by service-name the change is data-only; otherwise add a Voice tab. The exact path is decided during planning by reading the current page.
- **`Tokens.razor`**: no UI change. Cloud-provider cost events are tagged with `Origin = "voice"` so existing breakdowns light up automatically.
- **`HealthGrid.razor`**: shows `mcp-channel-voice`, `wyoming-whisper`, `wyoming-piper`. The channel emits heartbeat events for the two Wyoming backends it talks to.

### Server-side wiring

- `Observability/Services/MetricsQueryService.cs` — new voice grouping query methods.
- `Observability/MetricsApiEndpoints.cs` — new `/api/metrics/voice/...` endpoints.
- The SignalR hub already broadcasts every `MetricEvent`; the dashboard's `HubEventDispatcher` filters by event type, so the Voice page subscribes to voice events without hub changes.

## Error handling

| Failure | Behaviour |
|---------|-----------|
| Wake-word false trigger | STT returns empty/low-confidence; channel drops it before notifying the agent. Soft dismiss tone on the satellite. |
| STT timeout / backend down | After configurable timeout, satellite plays a pre-rendered "I didn't catch that" prompt cached on hub. `SttError` metric event published. |
| Agent error or no reply within timeout | Same dismiss-with-error prompt. `ChannelDeliveryFailed` metric event. |
| Satellite disconnects mid-utterance | Wyoming session closes; in-flight transcription cancelled; pending agent dispatch cancelled. |
| Reply arrives after satellite is gone | TTS generated then discarded. Voice is ephemeral; no offline queueing. |
| Two satellites wake concurrently | Independent Wyoming sessions, independent `channel/message` notifications, independent threads, independent replies. |
| Same satellite re-wakes during TTS playback (barge-in) | `wyoming-satellite` cuts playback and opens a new capture session. The previous TTS stream is cancelled. |
| Wyoming backend OOM at boot | Stock services fail fast with clear logs. The channel marks them unhealthy and the dashboard reflects it. |

## Testing

Per repo TDD rules (`.claude/rules/tdd.md`).

### Unit tests — `Tests/Unit/Channels/Voice/`

- `SatelliteRegistryTests` — id resolution, unknown-id rejection, per-satellite override layering.
- `WyomingSpeechToTextTests` — adapter wiring with mocked Wyoming client.
- `WyomingTextToSpeechTests` — same.
- `ApprovalGrammarParserTests` — yes/no/sí/no, edge cases such as "yes please cancel that".
- `ConfidenceGateTests` — empty and low-confidence transcripts dropped.

### Integration tests — `Tests/Integration/Channels/Voice/`

- End-to-end: feed a canned WAV through a fake Wyoming satellite client; assert `channel/message` notification fires with expected text, identity, room. Use a real `wyoming-faster-whisper` `tiny` model in a docker-compose fixture for speed.
- `send_reply` round-trip: invoke the tool; assert audio frames flow back to the fake satellite client.
- STT-provider switch: same scenario, swap configuration to `OpenAiSpeechToText` with a stubbed HTTP server, assert identical channel-side behaviour.

### E2E tests — manual, scripted

- One real Pi Zero 2 W satellite, hub local, agent in dev. Speak prompts; expect spoken replies. Smoke-test by playing pre-recorded utterances through a USB speaker into the satellite mic.

## Phasing

Each slice ends in a clean commit and a passing test suite.

### Slice 1 — `McpChannelVoice` skeleton

- New project mirroring `McpChannelTelegram`'s layout.
- MCP HTTP transport, dummy `send_reply` and `request_approval` tools, `SatelliteRegistry`.
- Compose entry, agent connects.
- One `voice.connected` heartbeat metric, so health appears immediately.
- No audio yet.

**Done when**: agent lists the channel; sending a `channel/message` from a stub routes through to a logging sink.

### Slice 2 — STT path

- Wyoming server in `McpChannelVoice` accepts inbound satellite connections.
- `ISpeechToText` + `WyomingSpeechToText`.
- Compose adds `wyoming-faster-whisper`.
- Voice page lands with the first two charts.
- Metric events: `WakeTriggered`, `UtteranceTranscribed`, `SttError`.

**Done when**: a real Pi Zero satellite (or a desktop running `wyoming-satellite`) wakes, speaks, and the agent receives the transcript as a `channel/message` from the configured identity. No reply yet.

### Slice 3 — TTS path

- `ITextToSpeech` + `WyomingTextToSpeech`.
- `send_reply` synthesises audio and streams back.
- Compose adds `wyoming-piper`.
- Metric events: `TtsLatencyMs`, `WakeToFirstAudioMs`. Latency KPI added to Overview.

**Done when**: agent reply is spoken back through the satellite. MVP demo state.

### Slice 4 — Approval over voice

- `ApprovalGrammarParser`, re-prompt, button-press fallback.
- `ApprovalResolved` metric event and chart on Voice page.

**Done when**: `request_approval` round-trips successfully on at least Spanish and English yes/no.

### Slice 5 — Cloud STT/TTS adapters

- `OpenAiSpeechToText`, `OpenAiTextToSpeech` behind the existing interfaces.
- Configuration-only switch; default stack unchanged.
- Cost events tagged with `Origin = "voice"` for token-page slicing.

**Done when**: switching `Voice.Stt.Provider` to `OpenAi` works end-to-end without code changes elsewhere.

## Out of scope (for this spec)

- Voice fingerprinting / per-speaker identity.
- Wake-word training pipeline (custom wake words use openWakeWord's existing Colab tool when needed).
- Music/media playback features.
- Whole-house intercom or satellite-to-satellite audio.
- Battery-powered satellites.

## Risks

- **Wake-word false-trigger rate** in noisy kitchens may be higher than expected. Mitigation: tune openWakeWord threshold per-satellite; offer button-press as alternate trigger.
- **Pi Zero 2 W audio latency** with USB mics can be quirky. Mitigation: ReSpeaker HAT (uses SPI, lower jitter) is the recommended default.
- **`large-v3` Whisper on CPU** can be too slow on a Pi 5. Mitigation: default to `base` or `distil-large-v3`; bumping is a one-line config change once hardware allows.
- **Multi-satellite concurrent load** on a Pi 5 hub. Mitigation: STT/TTS run as independent containers and can be moved to a separate box later without code changes.

## Style and layering rules to honour during implementation

- File-scoped namespaces, primary constructors for DI, `record` types for DTOs, `IReadOnlyList<T>` returns, LINQ over loops, `TimeProvider` for time-dependent code.
- No XML documentation comments. Comments only when explaining a non-obvious "why".
- Domain layer (`Domain/Contracts/`, `Domain/DTOs/Voice/`) imports nothing from `Infrastructure` or `Agent`. Concrete adapters live in `Infrastructure/Clients/Voice/`.
- New environment variables and configuration keys are added to `DockerCompose/docker-compose.yml`, `DockerCompose/.env` (secrets only), `appsettings.json`, and `appsettings.Development.json` in the same change.
