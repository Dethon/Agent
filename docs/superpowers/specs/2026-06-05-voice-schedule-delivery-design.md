# Voice schedule delivery — design

**Date:** 2026-06-05
**Branch:** voice-impl
**Status:** Approved (pending implementation plan)

## Problem

Today a fired schedule cannot be heard on a voice satellite. The scheduling
server turns each `deliverTo` entry into a `ReplyTarget(channelId, null)`;
`ChatMonitor` mints a conversation via `create_conversation` and streams the
agent's reply back with `send_reply`. The voice channel breaks this chain twice:

1. It exposes no `create_conversation` tool, so `McpChannelConnection.CreateConversationAsync`
   returns `null` and `ChatMonitor` drops the voice target before any reply is sent
   (`Domain/Monitor/ChatMonitor.cs:65`).
2. Even if a reply arrived, `send_reply` resolves a satellite only through
   `VoiceConversationManager._conversationToSatellite`, which is populated **only**
   by an inbound utterance. A schedule-minted conversation has no satellite, so the
   reply is a silent no-op (`McpChannelVoice/McpTools/SendReplyTool.cs:43-48`).

The only existing way to push audio to an idle-but-connected satellite is
`AnnouncementService`, reachable solely via the token-guarded HTTP
`POST /api/voice/announce` endpoint, which addresses satellites by id/room/all
— not by conversation id — and is not wired to scheduling.

## Goals

- Let a schedule deliver its result to voice, spoken on one or more satellites.
- Address a **specific satellite by id** or **all satellites** (no room concept).
- Voice delivery is **opt-in by the user only**: the agent adds a voice target
  *only when the user explicitly asked for a voice/spoken notification*. Default is
  silence (e.g. a schedule that starts the AC at 3am must not announce).
- Reuse the existing reply/announce machinery; voice becomes a first-class
  `deliverTo` channel rather than a special case.

## Non-goals

- Room-level or group targeting (only satellite id + all).
- Queuing/replaying a result for a satellite that is offline at fire time —
  offline satellites are silently skipped (logged).
- A hard code-level guard forcing the opt-in rule — it is a prompt instruction.
  Silence-by-default already holds structurally because `DefaultDeliverTo` is
  `["signalr"]` and voice is never injected there.
- Surfacing a live satellite catalog to the scheduling agent (possible follow-up).

## Chosen approach — voice as a real `deliverTo` channel

A `deliverTo` entry may be sub-addressed: `voice`, `voice:all`, or
`voice:<satelliteId>`. The satellite selector rides as an optional `Address` on
`ReplyTarget` and as a new `address` argument on `create_conversation`. The voice
channel gains a `create_conversation` tool that mints a real (WebChat-visible)
conversation and binds it to an `AnnounceTarget`; the agent's streamed reply then
flows through the normal `send_reply` path, which on stream-complete speaks the
accumulated text via `AnnouncementService` (full reuse of target resolution,
offline-drop, per-satellite playback, and metrics).

### End-to-end flow

```
schedule.json deliverTo: ["voice:fran-office-01"]   (or "voice" / "voice:all")
  │
  ▼ ScheduleFirePlanner — split "channelId:address" on first ':'
  ReplyTarget(ChannelId="voice", ConversationId=null, Address="fran-office-01")
  │
  ▼ ChatMonitor.ResolveDeliveryTargetsAsync — null ConversationId ⇒ mint
  channel.CreateConversationAsync(agentId, "Scheduled task", sender, prompt, address="fran-office-01")
  │
  ▼ Voice create_conversation tool (NEW)
  - parse address → AnnounceTarget { SatelliteId="fran-office-01" }  (null/"all" → { All=true })
  - validate configured satellite (unknown id ⇒ throw McpException ⇒ target dropped, nothing persisted)
  - ConversationFactory.CreateAsync(...) → persisted convId  (WebChat-visible, same as utterances)
  - VoiceDeliveryRegistry.Bind(convId, target)
  - return convId
  │
  ▼ agent (e.g. mycroft) runs prompt; reply streams via normal send_reply(convId, chunk...)
  │
  ▼ Voice send_reply — convId is not an utterance binding ⇒ scheduled branch
  - accumulate Text chunks (existing ReplyTextAccumulator, keyed by convId)
  - on StreamComplete: AnnouncementService.AnnounceAsync({ Target, Text=accumulated, Priority=Normal })
  - then VoiceDeliveryRegistry.Remove(convId)
```

## Detailed changes

### Contracts / data model

- **`Domain/DTOs/Channel/ReplyTarget.cs`** — add optional third positional
  parameter: `record ReplyTarget(string ChannelId, string? ConversationId, string? Address = null)`.
  Backward compatible: existing `new ReplyTarget(c, null)` still compiles, and a
  payload that omits `address` deserializes it as `null` (the default value).
  Serialized as part of `ChannelMessageNotification.ReplyTo` via
  `ChannelProtocol.SerializerOptions` (camelCase `address`).
- **`Domain/Contracts/IChannelConnection.cs`** — `CreateConversationAsync` gains
  `string? address` (before `CancellationToken`).
- **`Infrastructure/Clients/Channels/McpChannelConnection.cs`** — pass `address`
  through to the `create_conversation` tool args. Build the args inline as a
  `Dictionary<string, object?>` (mirroring `SendReplyAsync`) so the voice-specific
  `address` does not pollute `CreateConversationParams` / `IConversationFactory`.
  Keys: `agentId`, `topicName`, `sender`, `initialPrompt`, `address`.

### Scheduling

- **`McpServerScheduling/Services/ScheduleFirePlanner.cs`** — replace
  `channels.Select(c => new ReplyTarget(c, null))` with a split on the first `:`:
  `"voice:fran-office-01"` → `ReplyTarget("voice", null, "fran-office-01")`,
  `"voice"` → `ReplyTarget("voice", null, null)`, `"signalr"` → `ReplyTarget("signalr", null, null)`.
  The split is channel-agnostic; scheduling knows nothing about voice semantics.

### ChatMonitor

- **`Domain/Monitor/ChatMonitor.cs`** — at the mint call (`:53`), pass
  `target.Address` into `CreateConversationAsync`. `DeliveryTarget` is unchanged:
  once minted, the returned conversation id carries identity.

### Voice channel

- **`McpChannelVoice/McpTools/CreateConversationTool.cs`** (NEW) —
  `[McpServerTool(Name = ChannelProtocol.CreateConversationTool)]`, signature
  `(agentId, topicName, sender, services, initialPrompt = null, address = null)`.
  Steps:
  1. Parse `address` → `AnnounceTarget`: `null` or `"all"` → `{ All = true }`;
     otherwise `{ SatelliteId = address }`.
  2. For a specific id, validate against `SatelliteRegistry.GetById`; unknown id
     throws `McpException` so `McpChannelConnection` returns `null` and `ChatMonitor`
     drops the target (no persisted conversation, no audio).
  3. `IConversationFactory.CreateAsync(new CreateConversationParams { AgentId,
     TopicName, Sender, InitialPrompt })` → persisted conversation (WebChat-visible,
     identical to the utterance path).
  4. `VoiceDeliveryRegistry.Bind(convId, target)`.
  5. Return `convId`.
- **`McpChannelVoice/Services/VoiceDeliveryRegistry.cs`** (NEW) — singleton over a
  `ConcurrentDictionary<string, AnnounceTarget>`: `Bind`, `Resolve`, `Remove`.
  Bindings are removed on `StreamComplete`; a `TimeProvider`-based idle expiry
  (reusing `VoiceSettings.ConversationLifetime`) backstops leaks if a run dies
  before completing. Kept separate from `VoiceConversationManager`, which is
  strictly one-conversation-per-satellite for utterances and must not be evicted
  by a scheduled `all` delivery.
- **`McpChannelVoice/McpTools/SendReplyTool.cs`** — after the existing
  `VoiceConversationManager.ResolveSatelliteId` (utterance) miss, check
  `VoiceDeliveryRegistry.Resolve(convId)`. If bound:
  - Accumulate `Text` chunks via the existing `ReplyTextAccumulator`.
  - On `StreamComplete`: flush and call
    `AnnouncementService.AnnounceAsync(new AnnounceRequest { Target, Text, Priority = Normal })`,
    catching `AnnounceTargetNotFoundException` (log), then `Remove(convId)`.
  - `Reasoning` / `ToolCall` ignored; `Error` chunks are **dropped (logged), not
    spoken** — consistent with "silence preferred."
  The utterance path is unchanged.
- **`McpChannelVoice/Modules/ConfigModule.cs`** — register `VoiceDeliveryRegistry`
  and add `.WithTools<CreateConversationTool>()`.

### Prompt guidance

- **`Domain/Prompts/SchedulingPrompt.cs`** — extend the `deliverTo` documentation:
  - Syntax: `"voice"` / `"voice:all"` = every satellite; `"voice:<satelliteId>"` =
    one satellite.
  - State the rule firmly: add a voice target **only when the user explicitly asked
    for a voice/spoken notification**; otherwise omit voice — silence is the default.
    Include the example: *a schedule that starts the AC at night must NOT announce.*
  - Note that voice speaks the agent's reply aloud and offline satellites are
    silently skipped.

## Edge cases & decisions

- **Offline / unknown satellite.** Configured-but-offline satellite →
  `AnnouncementService` records `"offline"` (logged, no audio, no throw). Unknown
  satellite id → dropped at mint via `McpException`, nothing persisted. Both are
  silent (requirement).
- **Recurring schedules.** Each fire mints a fresh conversation + binding (the
  fired `ReplyTarget.ConversationId` is always `null`); the binding is removed on
  completion, so nothing accumulates.
- **Tool approvals during a voice-only schedule.** `ChatMonitor` binds the approval
  handler to `targets[0]`. If that is a voice target, `request_approval` cannot be
  answered (no utterance session) and resolves to *Rejected* — the tool call is
  denied but the run still completes and speaks its reply. **Mitigation
  (documented, not enforced):** to allow approvals, list a non-voice channel first,
  e.g. `["signalr", "voice:office"]` → approvals route to WebChat while voice still
  speaks. This is the existing group-bound-approval limitation, not new behavior.
- **Satellite-id discovery.** The scheduling prompt cannot enumerate live satellite
  ids (separate MCP server). The agent uses whatever id the user references, or
  `voice`/`voice:all` for "everywhere." Out of scope for this change.
- **WebChat visibility.** A deliberate side effect: voice scheduled deliveries
  persist a conversation (via the shared `ConversationFactory`) and therefore appear
  in WebChat, the same as live voice conversations.

## Testing (TDD, red → green)

- `ScheduleFirePlanner`: `"voice:fran-office-01"` → `ReplyTarget("voice", null,
  "fran-office-01")`; bare `"voice"` → `("voice", null, null)`; non-voice entries
  unaffected.
- `ChatMonitor`: `Address` is threaded into `CreateConversationAsync` (extend
  `ChatMonitorDeliveryTests`).
- Voice `CreateConversationTool`: address parsing (id / `all` / null), unknown-id
  throws, binding registered, convId returned.
- Voice `SendReplyTool`: a bound convId accumulates then announces on
  `StreamComplete` via a faked `AnnouncementService`; an error chunk is not spoken;
  the binding is removed afterward; the utterance path still works.
- `VoiceDeliveryRegistry`: bind / resolve / remove and idle expiry via a fake
  `TimeProvider`.

## Files touched

**New:** `McpChannelVoice/McpTools/CreateConversationTool.cs`,
`McpChannelVoice/Services/VoiceDeliveryRegistry.cs` (+ tests).

**Modified:** `Domain/DTOs/Channel/ReplyTarget.cs`,
`Domain/Contracts/IChannelConnection.cs`,
`Infrastructure/Clients/Channels/McpChannelConnection.cs`,
`McpServerScheduling/Services/ScheduleFirePlanner.cs`,
`Domain/Monitor/ChatMonitor.cs`,
`McpChannelVoice/McpTools/SendReplyTool.cs`,
`McpChannelVoice/Modules/ConfigModule.cs`,
`Domain/Prompts/SchedulingPrompt.cs`.

No new environment variables or docker-compose changes — satellites are already
configured and `AnnouncementService` is already registered.
