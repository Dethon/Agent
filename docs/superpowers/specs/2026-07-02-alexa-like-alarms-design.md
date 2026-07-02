# Alexa-like Alarm Improvements — Design

**Date:** 2026-07-02
**Status:** Approved design, pending implementation plan
**Builds on:** the voice alarms & reminders feature (HA calendar → insistent announce, wake-word acknowledgment) shipped on `alarms-and-reminders`

## Summary

Six hub-side improvements that close the felt distance between the current insistent-announce
alarms and Alexa-grade alarms/timers:

1. **Overlapping-alert fix** — one wake dismisses *everything* ringing on a satellite.
2. **Alarm & timer tones** — each repeat rings a generated tone before the spoken message.
3. **Volume ramp** — repeats start quieter and ramp to full volume.
4. **Snooze via context injection** — "five more minutes" works after a dismissal, in any
   language, with no phrase matching.
5. **Hub-local timers** — "set a 5-minute timer" gets a `/timers` VFS with sub-minute
   precision and no LLM in the fire path.
6. **Ack-gated escalation** — unacknowledged alarms POST an HA webhook so HA can notify
   another channel.

All work is in the .NET solution (`McpChannelVoice`, `Domain`, config). The Rust satellite is
untouched.

## Non-goals (explicitly out of scope)

- **Durable alerts / retry-on-reconnect** (user deferred). In-flight alerts and armed timers
  die with the hub; offline satellites still just get `AlarmOffline`.
- **Satellite-local firing** (user deferred). HA/hub remain the timing owners.
- **Alarm-specific LED pattern** (user deferred). Needs a Wyoming protocol extension
  (announcement-kind label on `audio-start`) plus a new `LedState` in the Rust crate; alarm
  audio keeps lighting the existing Speaking state.
- **Continuous ringing.** Deliberate non-goal, not a shortcut: the satellites have no AEC, so
  a continuous loud ring would mask the wake word that dismisses it (same physics as
  wake-over-music). Repeats-with-silence-gaps is the correct model for this hardware.
- **Button dismissal work.** Already works today: the satellite button feeds the same
  `start_turn` → `run-pipeline` path as wake (`satellite/src/satellite/state_machine.rs:174`),
  and the hub acknowledges alerts on `run-pipeline`. The hub cannot distinguish button from
  wake, so the existing wake-ack tests cover it.

## Decisions (resolved with the user)

| Topic | Decision |
|-------|----------|
| Timer surface | **VFS `filesystem://timers`** on a dual-role `McpChannelVoice` (like `mcp-scheduling`), not MCP tools, not an extension of `/schedules`. |
| LED scope | Deferred; hub-only round. |
| Escalation | **Global webhook URL**, fires for every unacknowledged **alarm**; timers never escalate; HA owns the policy. No per-alarm flag. |
| Snooze | Context injection through the channel protocol + LLM; bare wake + silence still just dismisses. |
| Timer durability | None (in-memory store); consistent with the deferred-durability decision. |

## 1. Overlapping-alert fix (`ActiveAlertRegistry`)

Today `ActiveAlertRegistry` maps satellite id → **single** `AlertHandle`, and `Register`
overwrites: when a 9:00 alarm and a 9:00 reminder both target the kitchen, the wake word
acknowledges only the newer one and the older keeps looping, undismissable by voice until its
cap. This is the closest thing to a bug in the current feature.

**Change:** map satellite id → **set of handles**.

- `Register(handle)` adds the handle to each targeted satellite's set.
- `Acknowledge(satelliteId)` cancels **all** handles in that satellite's set (Alexa's "stop
  stops everything ringing") and returns the dismissed alerts' descriptions (text + kind) for
  snooze context (section 4). Each handle's shared CTS still cancels the alert across all its
  satellites.
- `Discard(handle)` removes only that handle from each of its satellites' sets (a newer alert
  registered on the same satellite is unaffected).

Concurrency stays lock-based-or-CAS inside the registry (implementation detail); the public
surface only changes `Acknowledge`'s return type.

## 2. Alarm & timer tones + cadence defaults

A fired alarm currently rings as *just a voice* — easy to miss, doesn't register as an alarm.

**`AlarmTone`** (new, `McpChannelVoice/Services/`): generated PCM at 22 050 Hz mono S16LE
(the fixed satellite sink rate), same no-asset-file pattern as `ListeningChime`. Two patterns:

- **Alarm**: an urgent multi-pulse pattern (~1 s).
- **Timer**: a faster triple-beep, audibly distinct from the alarm (Alexa distinguishes them).

**`AnnounceRequest`** gains an optional `Kind` (`alarm` | `timer`; default `alarm` when
`Insistent` is present) so `InsistentAnnouncementController` picks the tone. HA-bridged
alarms need no payload change — insistent requests default to alarm-kind.

**`InsistentAnnouncementController.BufferAudioAsync`** prepends the tone chunks to the
buffered TTS, so every round plays *tone + message* on every targeted satellite. Synthesis
still happens once per alert.

**Cadence defaults** (`InsistentDefaults`): `GapSeconds` 30 → **15**, `MaxRepeats` 5 →
**12** — a ~3.5-minute ring window, closer to Alexa. Per-event overrides via the calendar
event's `insistent` JSON are unchanged. Timer firings use timer-specific defaults
(section 5).

## 3. Volume ramp

Per-round gain applied to the buffered PCM (tone + speech) before enqueueing:

- Round 1 plays at `RampStartPercent` (default **50**); gain rises linearly to 100 % by round
  `RampRounds` (default **4**) and stays there.
- New `PcmGain` helper: 16-bit saturating sample scale over `AudioChunk` data.
- Config lives in `InsistentDefaults` (`RampStartPercent`, `RampRounds`). No per-event knob
  (YAGNI). `RampStartPercent: 100` disables the ramp.

## 4. Snooze via context injection

Language-agnostic snooze with no vocabulary matching, honoring the "generic, non-blocking
STT" rule — the LLM does the understanding:

1. `AlertHandle` carries the alert's **text** and **kind** (alarm/timer).
2. On wake-ack (`run-pipeline`), `WyomingSatelliteHost` takes the dismissed descriptions
   returned by `Acknowledge` and stashes them on the `SatelliteSession` with a timestamp.
3. When a transcript dispatches from that satellite within **60 s** of the dismissal,
   `TranscriptDispatcher` forwards the description as a new optional **`DismissedAlert`**
   field on `ChannelMessageNotification` (same protocol shape as `Location`/`SatelliteId`:
   nullable, only the voice channel populates it), then clears the stash. The stash is
   cleared after first use or expiry — one transcript gets the context.
4. `ChatMonitor` copies it onto the user message's properties (`ChatMessageExtensions`,
   like `SetLocation`); `OpenRouterChatClient` renders it into the existing bracketed
   per-message prefix: `[The user just dismissed the alarm "Take out the trash"]`.
5. Prompt updates teach the idiom:
   - `HomeAssistantPrompt` alarms section: to snooze/repeat a dismissed **alarm**, create a
     new one-shot calendar event at the requested offset.
   - `TimerPrompt` (new, section 5): to extend a dismissed **timer**, create a new timer.

Bare wake + silence still just dismisses (no transcript → no context → nothing happens).
The fallback ack path (`Acknowledge` after a dispatched transcript, for turns where no wake
event was observed) runs *after* that transcript has dispatched, so its context lands on the
next transcript within the window — e.g. a follow-up utterance.

Chat-history note: `DismissedAlert` rides `AdditionalProperties` like `Location` already
does; both history readers (`RedisThreadStateStore`, `RedisStateService`) tolerate unknown
optional properties, and the `MemoryContext` JSON round-trip fix established the
string-property pattern.

## 5. Hub-local timers (`/timers` VFS, dual-role voice server)

"Set a timer for 5 minutes" cannot be an HA calendar event: HA calendar triggers have
~minute granularity and the create → automation → REST hop adds latency. Timers become a
first-class hub-local feature; `McpChannelVoice` goes **dual-role** (channel + filesystem
server), the established pattern from `mcp-scheduling`.

### Domain (`Domain/Tools/Timers/Vfs/`, contracts, DTOs)

- **`TimerFileSystem`** — `IFileSystemBackend`, `FilesystemName = "timers"`, typed
  `FsResult<T>`. Layout:
  - `/timers/<timerId>/timer.json` — write to create/arm:
    `{ "durationSeconds": 300, "text"?: "pasta is ready", "target": { room | satelliteId | satelliteIds | all } }`.
    `<timerId>` is a descriptive id the agent chooses (e.g. `pasta`); the spoken text
    defaults to "<timerId> timer" when `text` is omitted.
  - `/timers/<timerId>/status.json` — read-only: `{ "remainingSeconds", "firesAt" }`
    (`firesAt` rendered in the operating timezone, like `/schedules`).
  - `fs_delete` on the timer directory cancels. `move`/`exec` unsupported (same envelope as
    the print queue).
- **`ITimerStore`** (Domain contract) — arm/list/get/cancel; the VFS engine and the fire
  service both depend on it.
- **`TimerPrompt`** (`Domain/Prompts/TimerPrompt.cs`) — teaches the idiom: timers are
  short-lived kitchen-scale countdowns; default `target` to the speaking room; alarms and
  calendar reminders stay on the HA calendar (`calendar.assistant_alarms`); querying time
  left = read `status.json`; extend-after-dismiss = create a new timer.

### Hub (`McpChannelVoice`)

- **In-memory `ITimerStore`** implementation (no durability, by decision).
- **`TimerFireService`** — `BackgroundService`, `TimeProvider`-driven, ~1 s resolution. On
  expiry: build an `AnnounceRequest` (timer kind, timer text, stored target, insistent with
  **timer defaults: gap 10 s, `MaxRepeats` 12**) and call
  `InsistentAnnouncementController.StartAsync` **in-process** (no HTTP hop), then remove the
  timer. Ringing, wake/button dismissal, overlap handling, ramp, and snooze context all
  reuse the alert machinery.
- **`filesystem://timers` resource** — `McpResources/FileSystemResource.cs`, same shape as
  the scheduling/printer servers.

### Wiring

- Add `mcp-channel-voice` to the agent's `mcpServerEndpoints`; the existing dual-role
  mechanism hides the channel-protocol tools (`send_reply`, `request_approval`,
  `register_agents`) from the LLM.
- Config skeleton (docker-compose + appsettings) lands in the same change, per the repo
  rule. No new secrets.

## 6. Ack-gated escalation webhook

When an **alarm** (never a timer) reaches its cap unacknowledged:

- New optional `AnnounceSettings.Escalation.WebhookUrl` (e.g.
  `http://homeassistant:8123/api/webhook/alarm-unacked`). Unset ⇒ feature off.
- `InsistentAnnouncementController`'s unacknowledged branch POSTs
  `{ "text", "satellites", "rounds" }` via `IHttpClientFactory`, fire-and-forget with a
  logged warning on failure; no retry. The `AlarmUnacknowledged` metric is unchanged.
- `docs/home-assistant-alarms.md`: add the webhook automation example (webhook trigger →
  `notify.mobile_app_*`) and retire the "parallel notify fires regardless" workaround note.
- Non-secret config: webhook URL goes in appsettings + compose environment.

## Error handling

| Case | Behavior |
|------|----------|
| Tone generation / gain failure | Degrade to plain TTS ring; log; never block the alert. |
| TTS synthesis failure | Unchanged (alert loop logs and exits). |
| Webhook POST failure | Warning log; no retry; metric unchanged. |
| Timer with invalid duration (≤ 0) or unresolvable target | `fs_create`/edit rejected with a typed VFS error (target resolution reuses the controller's rules). |
| Timer fires while its satellite is offline | Same as alarms: `AlarmOffline` metric, nothing spoken (no durability by decision). |
| Hub restart | Armed timers and in-flight alerts are lost (documented; durability deferred). |
| Two alerts overlap on a satellite | Both ring (queued by priority); one wake dismisses both. |

## Testing (TDD — Red/Green/Refactor)

- **Unit** (`Tests/Unit/McpChannelVoice`, `Tests/Unit/Domain`):
  - Registry: overlapping handles on one satellite; `Acknowledge` cancels all and returns
    descriptions; `Discard` removes only its own handle; sibling-satellite cancellation.
  - `AlarmTone`: 22 050 Hz output, distinct alarm/timer patterns, bounded amplitude.
  - `PcmGain`: scaling, saturation, ramp schedule across rounds.
  - Controller: tone prepended per kind; ramp applied per round; escalation POSTed only for
    unacknowledged alarms (never timers, never acknowledged).
  - Snooze: host stashes dismissal on wake-ack; dispatcher forwards within 60 s and clears;
    expiry drops it; `ChatMonitor` property copy; `OpenRouterChatClient` prefix rendering.
  - `TimerFileSystem` journey tests mirroring `ScheduleFileSystemJourneyTests`
    (create/list/status/cancel/errors, zone-rendered `firesAt`).
  - `TimerFireService` with `FakeTimeProvider`: fires on expiry, removes the timer, uses
    timer insistent defaults.
- **Integration** (`Tests/Integration/McpChannelVoice`, style of
  `InsistentAnnounceE2ETests`): timer armed via the VFS → fires → rings with tone → wake
  dismisses; overlapping alarm+timer dismissed by one wake.

## Implementation order

1. Registry overlap fix (bug-adjacent, everything else builds on `Acknowledge` returning
   descriptions).
2. Tones + `Kind`.
3. Volume ramp.
4. Snooze context (protocol → channel → monitor → chat client → prompts).
5. `/timers` (Domain engine → hub store/fire service → resource → dual-role wiring →
   `TimerPrompt`).
6. Escalation webhook + `docs/home-assistant-alarms.md` update.

## Files touched (indicative)

**New:** `McpChannelVoice/Services/AlarmTone.cs`, `McpChannelVoice/Services/PcmGain.cs`,
`McpChannelVoice/Services/TimerFireService.cs`, `McpChannelVoice/Services/InMemoryTimerStore.cs`,
`McpChannelVoice/McpResources/FileSystemResource.cs`,
`Domain/Tools/Timers/Vfs/TimerFileSystem.cs`, `Domain/Contracts/ITimerStore.cs`,
`Domain/DTOs/Voice/TimerSpec.cs`, `Domain/Prompts/TimerPrompt.cs`.

**Modified:** `ActiveAlertRegistry`, `InsistentAnnouncementController`, `InsistentPlan`/
`InsistentDefaults`, `AnnounceRequest` (+`Kind`), `WyomingSatelliteHost`,
`TranscriptDispatcher`, `SatelliteSession`, `ChannelMessageNotification` (+`DismissedAlert`),
`ChatMonitor`, `ChatMessageExtensions`, `OpenRouterChatClient`, `HomeAssistantPrompt`,
`McpChannelVoice/Modules/ConfigModule.cs`, `AnnounceSettings`, agent `mcpServerEndpoints`
config, `DockerCompose/docker-compose.yml`, appsettings, `docs/home-assistant-alarms.md`.
