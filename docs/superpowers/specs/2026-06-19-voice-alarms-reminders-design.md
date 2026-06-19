# Voice Alarms & Reminders — Design

**Date:** 2026-06-19
**Status:** Approved design, pending implementation plan
**Author:** brainstormed with the user

## Summary

Alarms and reminders for the voice agent are delivered as **insistent spoken messages**:
the message is spoken on the target satellite(s) and repeats on a fixed gap until the user
acknowledges it by speaking, or until a safety cap is reached. There is **no ringtone**, **no
on-device audio**, and **no snooze**.

The **timing/trigger layer is owned by Home Assistant** (HA), not the in-house scheduler. The
agent creates a calendar event in HA; when the event is due, an HA automation POSTs the message
to the voice hub's existing announce endpoint in a new *insistent* mode. The **insistent delivery
loop (repeat-until-acknowledged) is owned by the voice hub**, because acknowledgment happens on
our satellites and HA has no visibility into it.

## Goals

- Let the agent set one-shot, relative ("in 20 minutes"), and recurring alarms/reminders by voice.
- Speak them insistently on one or more satellites, repeating until acknowledged or capped.
- Acknowledgment is **language-agnostic**: any detected utterance dismisses the alert. No phrase
  recognition.
- When the alert targets multiple satellites, **acknowledging on any one** stops it on all of them.
- Correct local-time / DST / recurrence handling, for free, by delegating timing to HA.

## Non-goals (explicitly out of scope for v1)

- **Snooze.** Removed by design — it would require recognizing a specific spoken phrase, which is
  unreliable across STT errors and languages.
- **Ringtone / looping audio / on-device alarm sounds.**
- **Conditional escalation** ("notify another channel only if unacknowledged"). This needs an
  ack-callback from the hub to HA; deferred. (HA can still fire an *unconditional* parallel notify.)
- **Hub retry-on-reconnect** for satellites offline at fire time.
- **HA-driven agent wake** (HA WebSocket event ingestion into the agent). Not needed for this design.
- **Changes to the in-house scheduler** (`McpServerScheduling`). It remains for agent-task schedules.

## Decisions (resolved during brainstorming)

| Topic | Decision |
|-------|----------|
| Alarm vs reminder semantics | Both are *insistent spoken messages*; no mechanical distinction beyond per-event urgency params. |
| Stop model | Repeat-until-acknowledged, capped. |
| Acknowledgment | **Any detected utterance** on any targeted satellite. Voice-activity based, language-agnostic. No phrase matching. |
| Snooze | **Removed.** |
| Multi-satellite | Alert spans all targeted online satellites; **first utterance on any one acknowledges and cancels all**. |
| Trigger/timing layer | **Home Assistant** (local calendar + bridging automation → announce endpoint). |
| Timezone/DST/recurrence | Handled by HA; no change to our `Schedule` DTO. |
| Event encoding | **Per-event JSON** in the calendar event `description` carries target + urgency params. |
| Miss handling | Hub emits a metric; HA automation may fire a parallel `notify` for redundancy. |

## Architecture

Three actors with clean seams:

```
AGENT (create)                 HOME ASSISTANT (when)                VOICE HUB (how)
──────────────                 ─────────────────────               ───────────────────────
calendar.create_event   →      [local "Alarms" calendar]           POST /api/voice/announce
  summary = message            event start fires                     { target, text,
  start  = absolute time   →    bridging automation                    insistent: {
  rrule  = recurrence (opt)      ├─ parse description JSON                gapSeconds,
  description = JSON params       └─ rest_command POST  ───────────→      maxRepeats | maxDurationSeconds
                                                                        } }
                                                                     InsistentAnnouncementController
                                                                      round = play(all) → openMic(all)
                                                                              → first-utterance ⇒ ack+cancel-all
                                                                              → all-silent ⇒ wait gap, repeat
                                                                              → cap ⇒ miss metric
```

- **HA owns timing** (timezone, DST, recurrence) and **cannot** observe acknowledgment.
- **The hub owns insistent delivery** and acknowledgment.
- **The agent owns content + scheduling intent** and is *not* in the per-repeat loop. The agent runs
  only at *create* time; it is not invoked when the alarm fires.

### Why HA-via-announce works (and why my initial "HA can't" verdict was wrong)

The announce endpoint (`McpChannelVoice/Services/AnnounceEndpoint.cs`, `POST /api/voice/announce`)
is **agent-independent**: token-authenticated, returns `202` immediately, and synthesizes TTS +
enqueues satellite playback on the hub's own playback loop — no agent, no MCP, no LLM in that path.
HA does **not** need to wake our agent; it only needs to reach this endpoint via a `rest_command`.
HA's own voice (Assist/Voice PE) is irrelevant here — HA calls *our* endpoint, which uses *our* TTS
and *our* satellites.

The HA integration (`Infrastructure/Clients/HomeAssistant/HomeAssistantClient.cs`) is REST/pull-only
(no WebSocket, no webhook ingestion), so HA cannot push events *into* the agent — but this design
does not require that. HA pushes to the announce endpoint instead.

## Component design

### 1. HA side (one-time provisioning, documented like the HA-token setup)

Provisioned in HA, not in this repo. Documented in `CLAUDE.md` (HA setup section):

- **A dedicated local calendar**, e.g. `calendar.assistant_alarms`.
- **A `rest_command`** that POSTs to the hub announce endpoint:
  - URL: `http://mcp-channel-voice:6015/api/voice/announce` (compose network).
  - Header: `X-Announce-Token: <shared announce token>`.
  - Body: JSON built from the event's `summary` (→ `text`) and `description` (→ `target` + `insistent`).
- **A bridging automation**: `trigger: calendar`, `entity_id: calendar.assistant_alarms`,
  `event: start` → parse `trigger.calendar_event.description` as JSON → call the `rest_command`.
  - Optionally add a *parallel* `notify.*` action for redundancy (the user's "HA-side parallel notify").

> **Validation items (HA-version dependent):** confirm the local calendar supports
> `calendar.create_event` with `rrule`, and `calendar.get_events` / `calendar.delete_event` /
> `calendar.update_event` for management. Confirm the automation can template a JSON body from the
> event description.

### 2. Insistent mode on the announce endpoint

- Extend `Domain/DTOs/Voice/AnnounceRequest.cs` with an optional nested descriptor, e.g.
  `InsistentOptions? Insistent` (new DTO). `null` ⇒ existing one-shot announce behavior is unchanged.
- `InsistentOptions`: `{ int GapSeconds, int? MaxRepeats, int? MaxDurationSeconds }`.
  The cap is `MaxRepeats` and/or `MaxDurationSeconds`; if both are set, whichever is reached first
  stops the alert; if both are null, a default `MaxRepeats` applies.
- `AnnounceEndpoint`: if `Insistent != null`, hand off to `InsistentAnnouncementController` instead
  of the one-shot `AnnouncementService.AnnounceAsync` path. Token auth and `202`-return are unchanged
  (the loop runs detached on the hub).
- Insistent playback uses `AnnouncePriority.High` (preempts lower-priority audio).

### 3. `InsistentAnnouncementController` (the core, new)

`McpChannelVoice/Services/InsistentAnnouncementController.cs`. Per insistent alert:

1. Resolve `AnnounceTarget` → the set of **online** targeted satellites
   (`SatelliteRegistry` + `SatelliteSessionRegistry`).
   - None online → emit `AlarmOffline` metric, return.
2. **Synthesize the TTS once** and reuse the audio bytes across rounds and satellites (avoid
   re-synthesizing every repeat). Audio is 22 050 Hz mono S16LE (satellite sink constraint).
3. Loop rounds `1..MaxRepeats` (or until `MaxDurationSeconds` elapses), under one shared
   `CancellationTokenSource` for the whole alert:
   - For each online targeted satellite: enqueue the cached audio as a **High-priority** `PlaybackJob`;
     after playback drains, **open a wake-free mic window** and run capture.
   - **Acknowledgment = any detected utterance** in the mic window (via `SilenceGate` voice-activity;
     optionally require a non-empty STT result purely as an ambient-noise guard — never phrase matching).
   - The **first satellite** to report an utterance triggers the shared cancellation: cancel in-flight
     playback and mic windows on the *other* satellites, stop further repeats, emit
     `AlarmAcknowledged` (record which satellite/round).
   - If all satellites' windows close with only silence → wait `GapSeconds`, next round.
4. Cap reached without ack → emit `AlarmUnacknowledged` metric.

**Live-conversation rule:** if a targeted satellite already has a live conversation/turn active when
the alert fires, treat the user as **present** — the alert is acknowledged for that satellite
immediately. Play the message once at High priority as a heads-up; do not open a competing mic loop
on that satellite. (If the live conversation is the only target, the whole alert is acknowledged.)

### 4. Acknowledgment via voice-activity (no STT phrase matching)

Reuses the existing capture stack: `UtteranceCapture` + `SilenceGate`. The win from dropping snooze
is that we never need to know *what* was said, only *that* the user spoke — which is
`SilenceGate.Decision.EndUtterance`. This is language-independent and immune to STT errors, and honors
the project's "generic, non-blocking STT" rule. STT may be used only to confirm a non-empty transcript
as a noise guard; it is never matched against a vocabulary.

### 5. Opening a mic window on an idle satellite (biggest implementation risk)

Today the mic only opens **after a user-initiated turn**, driven by `FollowUpConversation` over a live
`SatelliteSession`. The insistent controller must open a **wake-free capture window on an otherwise
idle satellite** (the `SatelliteSession` exists whenever the satellite is connected). This is the bulk
of the implementation effort:

- Extract/extend the wake-free capture path so it can be invoked for an announcement, not just within a
  conversation turn (reuse `UtteranceCapture` + `SilenceGate`; coordinate with the Wyoming read loop in
  `WyomingSatelliteHost`).
- Coordinate with `FollowUpConversation` so an alert and a live turn never fight over capture on the
  same satellite (see live-conversation rule).

### 6. Miss handling & metrics

Add pinned values to `Domain/DTOs/Metrics/Enums/VoiceMetric.cs` (values are explicitly pinned per the
voice-metric-int-serialization fix — append with explicit ints):

- `AlarmAcknowledged` — alert acknowledged (with satellite/round dimension).
- `AlarmUnacknowledged` — cap reached, no ack.
- `AlarmOffline` — no targeted satellite online at fire time.

Cross-channel escalation is left to the HA automation (optional parallel `notify`). Conditional
"escalate only if unacknowledged" is a future enhancement (needs hub→HA ack callback).

### 7. Agent-facing contract & prompt

The agent creates/manages alarms through the **existing HA VFS tools** (`fs_exec` → `CallServiceAsync`):

- **Create:** call `calendar.create_event` on the Alarms calendar entity with:
  - `summary` = the spoken message,
  - `start_date_time` = absolute time (agent resolves relative times to absolute; supplies HA-local
    time or an explicit offset),
  - `rrule` = recurrence (optional),
  - `description` = JSON: `{ "target": <AnnounceTarget>, "gapSeconds"?: int, "maxRepeats"?: int, "maxDurationSeconds"?: int }`.
- **List / cancel / edit:** `calendar.get_events` / `calendar.delete_event` / `calendar.update_event`.

Update the HA guide prompt (`Domain/Prompts/HomeAssistantPrompt.cs`, or a dedicated alarms prompt) to
teach this idiom, and steer the agent to use the Alarms calendar for human reminders/alarms rather than
the `/schedules` VFS (which stays for agent-task schedules).

> **Validation item:** confirm `HaArgParser` faithfully passes `summary`, `start_date_time`,
> `end_date_time`, `rrule`, and a quoted JSON `description` string through to the service call.

## Data flow

**Create:** user speaks → agent resolves the time → `fs_exec calendar.create_event …` → event stored
in HA's Alarms calendar.

**Fire:** HA calendar event starts → bridging automation parses the `description` JSON → `rest_command`
POST → `AnnounceEndpoint` (insistent) → `InsistentAnnouncementController` starts.

**Acknowledge:** controller plays on all targeted satellites → opens mic windows → first utterance on
any satellite → cancel-all → `AlarmAcknowledged`. All-silent rounds repeat on the gap until the cap →
`AlarmUnacknowledged`.

## Configuration & defaults

- New insistent defaults (in `AnnounceSettings` or a new `InsistentSettings`), overridable per event:
  - `GapSeconds` = 30
  - `MaxRepeats` = 5
  - mic window length ≈ follow-up `WindowMs` (~7 s)
- Shared announce token: must be reachable by HA config. Add a placeholder to `DockerCompose/.env`
  (secret) and document wiring it into HA's `rest_command`. The hub already reads `AnnounceSettings.Token`.
- No new agent/scheduler env vars (the scheduler is untouched).

## Error handling & edge cases

| Case | Behavior |
|------|----------|
| All targeted satellites offline | `AlarmOffline` metric; no playback. HA parallel `notify` (if configured) still fires. |
| Some satellites offline | Run on the online subset; ack on any online one stops all. |
| Live conversation active on a target | Treat as present → acknowledged for that satellite; play once as heads-up, no loop. |
| HA down | Alarms don't fire (documented dependency; HA is a core always-on service). |
| Recurrence | Each HA occurrence is an independent announce POST → independent alert; no state carries across occurrences. |
| HA double-fire / rest retry | Optional: dedupe by passing the calendar event UID as an alert id and ignoring an already-active id. |
| Ambient noise false-dismiss | Optional non-empty STT guard in the mic window; tune `SilenceGate` thresholds. |

## Testing strategy (TDD — Red/Green/Refactor)

- **Unit (`Tests/Unit/McpChannelVoice`):**
  - `AnnounceRequest` insistent parsing; `null` ⇒ unchanged one-shot path.
  - `InsistentAnnouncementController`: repeats on silence; stops at `MaxRepeats` / `MaxDurationSeconds`;
    first-utterance acknowledges; **multi-satellite first-ack cancels playback + mic on the others**;
    none-online ⇒ `AlarmOffline`; live-conversation ⇒ immediate ack + single heads-up.
  - Voice-activity ack uses `SilenceGate` outcomes; no vocabulary/phrase dependence.
  - TTS synthesized once and reused across rounds/satellites.
- **Integration (`Tests/Integration/McpChannelVoice`, see `AnnounceEndToEndTests.cs`):**
  - Insistent POST → fake satellite sessions → assert repeat/ack/cancel behavior and metrics.
- Follow the project's TDD rule: write the failing test first and capture the RED output before GREEN.

## Files touched (indicative)

**New:**
- `McpChannelVoice/Services/InsistentAnnouncementController.cs`
- `Domain/DTOs/Voice/InsistentOptions.cs`
- (possibly) a small capture-window abstraction extracted from the follow-up path.

**Modified:**
- `Domain/DTOs/Voice/AnnounceRequest.cs` (+ `Insistent`)
- `McpChannelVoice/Services/AnnounceEndpoint.cs` (route insistent)
- `McpChannelVoice/Services/AnnouncementService.cs` (synthesize-once reuse helper)
- `McpChannelVoice/Settings/AnnounceSettings.cs` (or new `InsistentSettings`) + defaults
- `McpChannelVoice/Services/{FollowUpConversation,SatelliteSession,UtteranceCapture}.cs`
  (invoke wake-free capture for an announcement; coordinate with live turns)
- `Domain/DTOs/Metrics/Enums/VoiceMetric.cs` (+ pinned `Alarm*` values)
- `Domain/Prompts/HomeAssistantPrompt.cs` (alarm-via-calendar idiom)
- `DockerCompose/.env` + HA setup docs in `CLAUDE.md` (shared announce token; provisioning steps)

**HA-side (documented, not in repo):**
- `calendar.assistant_alarms` local calendar
- `rest_command.voice_announce`
- the bridging automation

## Future enhancements

- Conditional escalation on no-ack (requires hub→HA ack callback).
- Hub retry-on-reconnect for satellites offline at fire time (pending-delivery queue keyed by satellite).
- Durable alerts surviving a hub restart mid-alert.
- Per-event UID dedupe against HA double-fire.
- Optional mirroring of alarms into the observability dashboard for visibility.

## Risks

- **Idle-satellite mic window** is genuinely new wiring over the Wyoming read loop and the existing
  turn/follow-up machinery; the live-conversation coordination is the subtle part.
- **HA arg-passing** through the VFS for `calendar.create_event` (JSON `description`, `rrule`,
  datetimes) needs validation and may require a small HA-tool enhancement.
- **HA provisioning** is manual one-time config — a place for misconfiguration; must be clearly documented.
