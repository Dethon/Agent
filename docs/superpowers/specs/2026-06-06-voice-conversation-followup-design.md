# Voice Conversation Mode — Wake-Free Follow-Up Turns — Design Spec

**Date**: 2026-06-06
**Status**: Approved for planning
**Owner**: Francisco Crespo

## Goal

After the agent finishes speaking a voice reply, automatically re-open the microphone for a few seconds so the user can speak a **follow-up without saying the wake word again**. If the user stays silent for the window, send nothing to the agent and fall back to normal wake-required mode.

The interaction chains into a natural back-and-forth ("**conversation mode**"): every spoken reply re-opens the window, and the conversation ends — falling back to wake — only when the user goes quiet for one window, an error occurs, or a safety cap is hit.

A second win comes for free: the same "speak → listen for the next utterance" primitive fixes the **voice approval flow** (`RequestApprovalTool`), which today needs the user to re-wake before answering *sí/no*.

## Background — current state

Satellites run **`wyoming-satellite`** (Rhasspy) on a Raspberry Pi 4 with on-device **`wyoming-openwakeword`**. Wake detection is on-device; mic audio only leaves the Pi after the wake word fires. The hub (`McpChannelVoice`) is the Wyoming **client** and dials out to each satellite at `tcp://<sat>:10800` (the satellite is the Wyoming *server*). This is deliberate: the LLM agent (`mycroft`) is the brain and voice is one MCP channel, so the team explicitly chose **not** to migrate to the ESPHome successor (linux-voice-assistant) that offers continue-conversation natively (decision recorded in `2026-06-06-satellite-media-player-design.md:244-255`).

Current turn lifecycle (`McpChannelVoice/Services/WyomingSatelliteHost.cs`):

1. **Wake → listen.** Inbound `run-pipeline`/first `audio-start` (`:184-186`) triggers `BeginUtterance` (`:230-261`): opens an unbounded `Channel<AudioChunk>` + a `SilenceGate`, publishes `WakeTriggered`, and spawns `TranscribeAndReplyAsync`.
2. **Stream → end-of-speech.** Each inbound `audio-chunk` (`:189-199`) feeds `SilenceGate.Process` (`WyomingProtocol/SilenceGate.cs:30-51`), which ends the utterance on trailing silence past `MinSpeechMs`, capped by `MaxUtteranceMs`.
3. **STT + re-arm.** `TranscribeAndReplyAsync` (`:263-316`) transcribes, then writes a **`transcript` event back to the satellite (`:283-284`)** — this stops the mic stream and **re-arms wake detection** — and finally dispatches the transcript to the agent (`:286`, `TranscriptDispatcher`). Note the satellite `transcript` is purely a satellite *control* signal; agent dispatch is separate.
4. **Reply → speak.** The agent streams reply chunks into `send_reply`; on `ReplyContentType.StreamComplete` (`McpTools/SendReplyTool.cs:83-85`) the accumulated text is synthesized and enqueued as a `reply:{satelliteId}` `PlaybackJob` (`SendReplyTool.cs:174-215`).
5. **Playback → idle.** `SatelliteSession.RunPlaybackLoopAsync` (`Services/SatelliteSession.cs:61-151`) writes outbound `audio-start`/`audio-chunk`/`audio-stop` and drains the job. The satellite is back in **armed/idle, wake-required**. There is no per-satellite state enum — "idle" is implicit.

## Verified facts that make this feasible (checked against `wyoming-satellite` source)

These were confirmed against `rhasspy/wyoming-satellite` `wyoming_satellite/satellite.py`:

1. **`WakeStreamingSatellite` streams from wake until it receives `Transcript`/`Error`/`Pause` — with no internal timeout and no local VAD that stops it on its own.** So if the hub **withholds** the `transcript`, the satellite keeps the mic stream open indefinitely.
2. **TTS audio plays normally while the mic stream is still open** — `AudioStart/Chunk/Stop` go straight to the sound service regardless of `is_streaming`. The price is echo: the open mic captures the agent's own voice.
3. **On `Transcript` the satellite stops streaming, plays its `done_wav`, and returns to wake detection.** That is exactly our "fall back to normal" signal, and it gives a natural end-of-conversation sound.
4. **No server event forces wake-free listening.** The satellite's `event_from_server` honors `RunSatellite`/`PauseSatellite`/`Transcript`/`Error`; a server-sent `Detection` only plays the "awake" sound and does **not** start streaming. So the only hub-side lever is *withholding the transcript* (fact 1) — there is no command to re-open a closed mic.

## Constraints and choices

- **Hub-only. Zero satellite/firmware/provisioning changes.** All logic lives in `McpChannelVoice`. (An on-device "continue shim" was considered and rejected for this scope — it touches fleet imaging on every Pi and is harder to test.)
- **Wake word still gates the *first* turn.** Conversation mode only removes the wake word for *follow-ups* within an active conversation. Pure VAD-streaming mode (no wake word at all) was rejected — it changes the product and worsens false triggers / music bleed.
- **Echo is the one real risk and must be handled in software.** The hardware has little AEC headroom (`2026-06-06-satellite-media-player-design.md:32`), so the hub must not feed its own playback back into STT.
- **Finalize-fast, no barge-in.** The mic is muted while the agent speaks; interrupting the agent mid-speech is out of scope.

## Approach — withhold the `transcript`, segment one held-open stream

Instead of sending the `transcript` immediately after the first utterance (which re-arms wake), the hub **defers** it. The entire conversation becomes **one held-open wake stream** that the hub segments server-side into successive utterances. The `transcript` is written **only to end the conversation** (fall back to wake).

```
wake word ─► utterance ─► agent turn ─► speak reply ─► [chime] ─► follow-up window
                                                            ▲              │
                                          user speaks ──────┘              │ silence (window expires)
                                                                           ▼
                                                          send transcript → satellite plays
                                                          "done" wav → re-arms wake (end)
```

Within the held-open stream the satellite streams continuously, so the hub controls turn-taking entirely by **what it does with the inbound `audio-chunk`s**:

- **While the agent thinks/speaks:** discard inbound chunks (mic "muted" — see Echo handling).
- **During a follow-up window:** feed chunks to a fresh `SilenceGate` that also enforces a **no-speech-start timeout**. Speech → a follow-up utterance (dispatched like any other). No speech before the timeout → end the conversation.

### Rejected alternatives

- **Server command to re-open the mic** — impossible; no such Wyoming event exists for `WakeStreamingSatellite` (verified fact 4).
- **On-device continue shim / event hooks** — robust and echo-free (mic opens *after* TTS) but requires per-Pi provisioning/systemd work and is hard to test in CI. Out of scope; retained as a future option if hardware echo proves intolerable.
- **Switch satellites to `VadStreamingSatellite`** — removes the wake word entirely; different product, worse false-trigger profile. Rejected.

## Architecture

Responsibilities are split into small, independently testable units. The current `WyomingSatelliteHost` read loop is large and untested; rather than thread more state through it, the per-connection turn logic is extracted into a **`ConversationCoordinator`**.

```
inbound audio-chunks ──► WyomingSatelliteHost (connection + frame demux)
                              │  routes each chunk to the coordinator's current
                              │  capture sink, or DISCARDS it when capture is suspended
                              ▼
                       ConversationCoordinator (per session — the turn state machine)
                          open capture (gate + no-speech timeout)
                            │ utterance ─► TranscriptDispatcher ─► agent
                            │                                         │ reply spoken (StreamComplete + playback drained)
                            │                                         ▼
                            │                                   chime ─► tail ─► reopen capture
                            └ empty window / cap / error ─► send `transcript` (end conversation, re-arm wake)
```

### Components

- **`SilenceGate`** (`WyomingProtocol/SilenceGate.cs`) — add a **no-speech-start timeout**: when `_speechStarted` is still false and elapsed ≥ `WindowMs`, return a result distinguishable from a real end-of-utterance (e.g. a new `Decision.NoSpeechTimeout`, or an `EndUtterance` plus a `SpeechObserved`/`WasEmpty` flag). Pure and unit-tested. `Reset()` already exists for reuse across turns.
- **`ConversationCoordinator`** (new, per `SatelliteSession`) — owns the turn state machine: `Listening → AgentTurn → Speaking → FollowUpWindow → …` and the end conditions. Decides "dispatch this utterance" vs "end the conversation." Uses `TimeProvider` for window/tail timing so it is deterministic in tests. Holds the per-conversation turn counter for the safety cap.
- **`WyomingSatelliteHost`** — becomes connection management + **frame demux**: hand each inbound `audio-chunk` to the coordinator's active capture sink, or drop it while capture is suspended; write the outbound `transcript` when the coordinator ends the conversation; keep the existing reconnect/teardown.
- **`SatelliteSession`** — add a `CaptureSuspended` flag (host honors it in the demux) and a **turn-completion signal** wired from the playback loop. The `PlaybackJob` record / `RunPlaybackLoopAsync` gains the currently-missing completion callback (`OnReplyDrained`/`OnTurnSpoken`) — today it only exposes `OnStarted`/`OnPreempted`/`onError` (`SatelliteSession.cs:7-12`, `:61-151`).
- **Chime generator** (new, small) — produces a short PCM earcon (e.g. ~150 ms tone with fade, 16 kHz/16-bit mono) in code, no asset file. Played as an ordinary `PlaybackJob` while capture is still suspended.

### Turn-completion coordination

A single agent turn can enqueue several playback jobs (interim narration during tool calls, then the final answer). The follow-up window must open only after the **whole turn** is spoken:

- `SendReplyTool`, on `ReplyContentType.StreamComplete` (`SendReplyTool.cs:83`), signals the session that the turn is complete — and whether anything was actually spoken (`FlushAndSpeakAsync` knows if the accumulated text was non-empty, `SendReplyTool.cs:159-172`).
- The session fires the post-turn action only once **(a)** the turn is marked complete **and** **(b)** the playback queue has fully drained:
  - **Turn spoke something →** run the post-reply sequence: chime → tail → open follow-up window.
  - **Turn was silent (no audio) →** end the conversation: send `transcript`, re-arm wake. (Nothing was said, so there is nothing to follow up on; chiming into silence would confuse.)

### Echo handling

The mic is physically open while the agent speaks, so:

1. **Suspend capture** (`CaptureSuspended = true`) for the entire spoken turn **and** the chime — inbound `audio-chunk`s are dropped in the host demux, fed to no gate.
2. **Tail guard** — after the last audio drains, wait `PlaybackTailMs` (default 400 ms) before resuming capture, to let speaker decay / room reverb settle and absorb the write-drain-vs-actual-playback latency (the hub knows socket write-drain, not speaker completion).
3. **Resume capture** with a freshly `Reset()` gate carrying the no-speech-start timeout.

`PlaybackTailMs` and the RMS speech threshold are the two knobs to tune on real hardware if residual echo falsely trips the window.

### Approval-capture integration (in scope)

`ApprovalCaptureBroker.WaitForUtteranceAsync` (`ApprovalCaptureBroker.cs:11-40`, 10 s window) + `RequestApprovalTool` (`RequestApprovalTool.cs:66-92`) already implement "speak prompt → window → capture next utterance," but the capture depends on a re-wake today. Wiring the approval prompt to the same held-open capture primitive makes *sí/no* answerable without re-waking. `TranscriptDispatcher.DispatchAsync` already routes a completed utterance to the broker first when `broker.HasListener` is true (`TranscriptDispatcher.cs`), so the two consumers compose: during an approval the captured utterance goes to the broker; otherwise it flows to the conversation. The chime is configurable per use (the spoken question is itself a cue for approvals).

### Conversation / thread continuity

Follow-up utterances dispatch through the existing path, so `VoiceConversationManager.GetOrCreateAsync` (5-min idle, renewed per utterance) keeps them in the **same conversation/thread** — preserving WebChat visibility via the shared `ConversationFactory`. No change needed.

## Configuration

New non-secret config (no new env vars/secrets → `appsettings.json` + `appsettings.Development.json`, surfaced on `VoiceSettings`, overridable per satellite on `SatelliteConfig`):

| Key | Default | Meaning |
|-----|---------|---------|
| `Voice:FollowUp:Enabled` | `true` | Master switch (global). |
| `Voice:FollowUp:WindowMs` | `7000` | How long to wait for the user to *start* speaking before falling back. |
| `Voice:FollowUp:PlaybackTailMs` | `400` | Echo guard after playback before opening the window. |
| `Voice:FollowUp:Chime` | `true` | Play the listening earcon before the window. |
| `Voice:FollowUp:MaxTurns` | `8` | Runaway cap — force fall-back after this many consecutive follow-up turns. |
| `Voice:Satellites:<id>:FollowUp:Enabled` | (inherits) | Per-satellite override. |

## Metrics

New `VoiceMetric` values (`Domain/DTOs/Metrics/Enums/VoiceMetric.cs`) published as `VoiceEvent`s for the observability dashboard:

- `FollowUpWindowOpened` — a window was offered after a reply.
- `FollowUpEngaged` — the user spoke in the window (became a follow-up turn).
- `FollowUpTimedOut` — the window expired silently (conversation ended).
- Conversation turn depth (count) for distribution analysis.

## Failure handling

- Any exception or `MaxTurns` cap in the coordinator → **send `transcript`** to release the satellite and re-arm wake (mirrors the existing safety re-arm at `WyomingSatelliteHost.cs:305-309`).
- A turn that produces no spoken output ends the conversation (re-arm), as above.
- Connection drop tears down naturally via the existing `finally` (`:214-227`); the satellite re-arms on reconnect.
- STT failure inside a follow-up turn behaves like today (empty transcript path), then re-arm.

## Out of scope

- **Barge-in** (interrupting the agent mid-speech). Mic is muted during playback; future extension.
- **On-device shim / firmware changes.** Hub-only by decision; revisit only if hardware echo proves intolerable.
- **Always-on / VAD-only listening** (no wake word at all).

## Testing (TDD)

- **Unit — `SilenceGate`:** no-speech-start timeout returns the empty/timeout result; existing end-of-speech behavior unchanged; `Reset()` reuse across turns.
- **Unit — chime generator:** produces valid 16 kHz/16-bit mono PCM of the expected duration.
- **Unit — `ConversationCoordinator`** (with `TimeProvider` + fakes): utterance → dispatch → reply-spoken → reopen loop; empty window → end (transcript); `MaxTurns` cap → end; silent turn → end; capture suspended during playback discards chunks.
- **Integration — `WyomingSatelliteHost`** with an in-memory fake `WyomingClient` simulating a continuously-streaming satellite: assert the `transcript` is **not** written after the first utterance, a follow-up utterance dispatches with **no** second wake, and the `transcript` **is** written after a silent window. Reuse the existing fixtures under `Tests/Integration/McpChannelVoice`.

## Key files touched

- `McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs` — no-speech-start timeout.
- `McpChannelVoice/Services/ConversationCoordinator.cs` — **new**, per-session turn state machine.
- `McpChannelVoice/Services/WyomingSatelliteHost.cs` — frame demux + capture suspension + deferred/closing transcript.
- `McpChannelVoice/Services/SatelliteSession.cs` — `CaptureSuspended` + turn-complete/`OnTurnSpoken` callback on the playback loop.
- `McpChannelVoice/Services/ListeningChime.cs` — **new**, PCM earcon generator.
- `McpChannelVoice/McpTools/SendReplyTool.cs` — signal turn complete (spoke / silent) on `StreamComplete`.
- `McpChannelVoice/McpTools/RequestApprovalTool.cs` + `Services/ApprovalCaptureBroker.cs` — use the shared post-speak capture window.
- `McpChannelVoice/Settings/VoiceSettings.cs`, `Settings/SatelliteConfig.cs` — `FollowUp` config block + per-satellite override.
- `Domain/DTOs/Metrics/Enums/VoiceMetric.cs` — new follow-up metrics.
- `appsettings.json`, `appsettings.Development.json` — `Voice:FollowUp:*` defaults.
- `Tests/Unit/McpChannelVoice/**`, `Tests/Integration/McpChannelVoice/**` — coverage above.
