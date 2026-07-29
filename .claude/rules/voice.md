---
paths:
  - "McpChannelVoice/**"
  - "satellite/**"
  - "scripts/provision-satellite-rs.sh"
  - "scripts/wsl-satellite*.sh"
  - "scripts/enroll-voice.sh"
---

# Voice Satellite Architecture

Voice is an MCP channel server (`McpChannelVoice`, channelId `voice`, container `mcp-channel-voice`, port 6015) plus hardware satellites. The hub is the Wyoming **client**: `WyomingSatelliteHost` dials every satellite with an `Address` in `VoiceSettings.Satellites` (`Satellites__<id>__Address`, e.g. `tcp://192.168.5.55:10800`) and reconnects forever; address-less satellites stay in the catalog as announce targets but are never dialed (announcements report offline).

Pipeline: satellite wakes locally → streams mic `audio-chunk`s → `SatelliteSession`/`SilenceGate` segment the utterance → **speaker-verification gate** (`Services/Verification`, ONNX embeddings scored against profiles enrolled under `/voices` via `scripts/enroll-voice.sh`; non-enrolled audio is dropped pre-STT, a conclusive match routes the speaker's folder name into the message sender for per-person memory) → optional **TSE** (`Services/Tse`, an STT decorator calling the `tse-extractor` container; `Tse__Mode` ∈ Off|Auto|Always, Auto gated by `NoiseFloorThreshold`) → **Lemonade STT** (OpenAI `/v1/audio/transcriptions`, Whisper-Medium on whisper.cpp; `STT_BACKEND` ∈ cpu|gpu, or the experimental NPU tier via `docker-compose.override.npu.yml` + `STT_MODEL`; decode quality via `STT_VAD_THRESHOLD`/`STT_INITIAL_PROMPT`/`STT_BEAM_SIZE` — defaults Silero VAD 0.6 + Castilian initial prompt + beam 5, empty disables, NPU/flm ignores them) → transcript dispatched as `channel/message` → reply spoken as it streams, segment-by-segment with prefetch, via **Lemonade Kokoro** (`/v1/audio/speech`, 24 kHz PCM resampled in-hub to 22 050 Hz) → back as `audio-start`/`audio-chunk`/`audio-stop`.

**Alert routing.** Insistent announces — timers and alarms, i.e. exactly the `/api/voice/announce`
requests carrying `insistent` — are marked `alert: true` on the Wyoming `audio-start` (protocol
1.5, `WyomingSatelliteHost.BuildAudioStart`; `InsistentAnnouncementController` is the only producer,
via `PlaybackJob.Alert`). The satellite plays a marked stream on `--alert-snd-command` instead of
`--snd-command`: on music units a non-attenuated `alert` ALSA softvol, so an alert is not capped by
the calibrated conversational `TTS` level. `AnnouncePriority.High` is deliberately not the marker —
approval prompts share it. The flag defaults to false everywhere, so ordinary replies, plain
announcements and a pre-1.5 satellite are unaffected, and an unopenable alert device falls back to
the normal sink rather than dropping the connection. The satellite's level chain is three per-source
softvols (`Music`, `TTS`, `Alert`) under a PipeWire master held at 100 %; see
`scripts/provision-satellite-rs.sh` for `TTS_VOLUME` / `ALERT_VOLUME`.

Sending a `transcript` event ends the satellite's turn and re-arms wake; `FollowUpConversation` reopens the mic wake-free, announced by the `ListeningChime` earcon and, on the wire, by a `listening-started` event (protocol 1.6) that returns the satellite's LED from Thinking to Listening — it cannot infer the moment itself, because its capture never closed. When several satellites hear the same wake word, `WakeArbiter` picks one winner (calibrated `wake_rms`, 500 ms coincidence window, onset-alignment check against open captures) and silently re-arms the losers via `pause-satellite`; a much-louder wake during another satellite's open conversation hands the conversation over.

**The satellite side is `satellite/CLAUDE.md` — read it before touching either side of the wire.** What the hub must respect: the satellite is the Wyoming **server** (the hub dials in), and its playback sink is FIXED 22 050 Hz mono S16LE regardless of announced rates, so all hub-emitted audio (TTS, `ListeningChime`) must be 22 050 Hz. The dockerized hub dials the dev satellite addresses only under `ASPNETCORE_ENVIRONMENT=Development` (`McpChannelVoice/appsettings.Development.json` overrides exactly the `Satellites` addresses; production points at the Pi IPs).
