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

Pipeline: satellite wakes locally → streams mic `audio-chunk`s → `SatelliteSession`/`SilenceGate` segment the utterance → **speaker-verification gate** (`Services/Verification`, ONNX embeddings scored against profiles enrolled under `/voices` via `scripts/enroll-voice.sh`; non-enrolled audio is dropped pre-STT, a conclusive match routes the speaker's folder name into the message sender for per-person memory) → optional **TSE** (`Services/Tse`, an STT decorator calling the `tse-extractor` container; `Tse__Mode` ∈ Off|Auto|Always, Auto gated by `NoiseFloorThreshold`) → **Lemonade STT** (OpenAI `/v1/audio/transcriptions`, Whisper-Medium on whisper.cpp; `STT_BACKEND` ∈ cpu|gpu, or the experimental NPU tier via `docker-compose.override.npu.yml` + `STT_MODEL`; decode quality via `STT_VAD_THRESHOLD`/`STT_INITIAL_PROMPT`/`STT_BEAM_SIZE`/`STT_BEST_OF`/`STT_SUPPRESS_NST`/`STT_VAD_SPEECH_PAD_MS`/`STT_VAD_MIN_SPEECH_MS` — defaults Silero VAD 0.6 + Castilian initial prompt + beam 5 + best-of 5 + non-speech-token suppression + 150 ms VAD padding + 150 ms VAD minimum speech, empty disables, NPU/flm ignores them) → transcript dispatched as `channel/message` → reply spoken as it streams, segment-by-segment with prefetch, via **Lemonade Kokoro** (`/v1/audio/speech`, 24 kHz PCM resampled in-hub to 22 050 Hz) → back as `audio-start`/`audio-chunk`/`audio-stop`.

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

**Short-phrase decode.** Three hub-side pieces target the one-to-three-second command, where whisper
has the least context and every gate is hardest on it. (1) Every transcription POSTs a `prompt`:
`WhisperPromptBuilder` composes `Stt.OpenAi.Prompt` — `{room}`/`{locality}` placeholders, per-satellite
overridable via `Satellites__<id>__Stt__OpenAi__Prompt` — followed by the prior segment's transcript,
capped at `MaxPromptChars` by trimming the prior text from its front so the configured vocabulary
survives whole. A per-request prompt replaces whisper-server's own `--prompt`, so the container
default only serves non-hub callers. (2) `Stt.Streaming` will not split before `FirstSplitAfterMs`
(4 s), so a short command is always decoded whole, and `ChainContext` feeds each fragment the
previous one's text — which serializes decodes, making `MaxInFlightDecodes > 1` inert (warned at
construction). (3) The gibberish gate's `avg_logprob` floor loosens to
`ShortSpeechAvgLogProbThreshold` below `FullThresholdSpeechMs` of measured speech, because a short
turn scores lower than a long one for reasons unrelated to being wrong. Measured on prod: a 2.9 s
clip scored −0.12 and a 0.75 s clip −0.23, and splitting one utterance in two produced a wrong verb
and a duplicated number.

**End-of-utterance floor.** `SilenceGate` classifies speech against a noise floor that `AdaptiveLevelTracker` measures inside the capture and freezes at the first accepted speech frame — so a capture that opens on sound (a command running straight on from the wake word) has nothing but that sound to measure. Two measurements taken where the room is actually audible cap it, and can only ever lower it: the satellite's own `room_rms` on `run-pipeline` (protocol 1.7, idle audio), and `RoomNoiseMemory`, a per-satellite window of recent background samples the hub keeps from its own captures (a no-speech capture's floor, or the trailing run that ended a capture — `WyomingClient.RoomLevelSamples` / `RoomLevelRetentionSeconds`). Without either the gate behaves exactly as before. An inflated floor is not a cosmetic error: it arms the adaptive regime, whose `PeakDropDb` backstop then reads normal syllable dynamics as background and endpoints the user mid-sentence.

Sending a `transcript` event ends the satellite's turn and re-arms wake; `FollowUpConversation` reopens the mic wake-free, announced by the `ListeningChime` earcon and, on the wire, by a `listening-started` event (protocol 1.6) that returns the satellite's LED from Thinking to Listening — it cannot infer the moment itself, because its capture never closed. When several satellites hear the same wake word, `WakeArbiter` picks one winner (calibrated `wake_rms`, 500 ms coincidence window, onset-alignment check against open captures) and silently re-arms the losers via `pause-satellite`; a much-louder wake during another satellite's open conversation hands the conversation over.

**The satellite side is `satellite/CLAUDE.md` — read it before touching either side of the wire.** What the hub must respect: the satellite is the Wyoming **server** (the hub dials in), and its playback sink is FIXED 22 050 Hz mono S16LE regardless of announced rates, so all hub-emitted audio (TTS, `ListeningChime`) must be 22 050 Hz. The dockerized hub dials the dev satellite addresses only under `ASPNETCORE_ENVIRONMENT=Development` (`McpChannelVoice/appsettings.Development.json` overrides exactly the `Satellites` addresses; production points at the Pi IPs).
