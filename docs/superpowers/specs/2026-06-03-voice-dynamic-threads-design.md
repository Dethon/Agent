# Dynamic per-satellite voice threads with idle lifetime and WebChat visibility

**Date:** 2026-06-03
**Branch:** voice-impl
**Status:** Approved (design)

## Problem

The voice channel posts every utterance from a satellite into a single, permanent
thread. `SatelliteSession.ConversationId => SatelliteId`
(`McpChannelVoice/Services/SatelliteSession.cs:28`) hardcodes a 1:1 mapping between a
satellite and one conversation that never resets. Consequences:

- The agent accumulates unbounded context for a satellite — a question asked days
  apart shares one ever-growing thread.
- Voice-initiated conversations do not appear in the WebChat UI at all (no
  `TopicMetadata` is ever persisted for them).

## Goals

1. **Dynamic thread selection per satellite** with a 5-minute idle lifetime:
   - The first utterance after an idle gap mints a fresh conversation.
   - Every utterance renews the idle timer.
   - When the timer expires, the conversation is cancelled and cleaned up in memory.
   - The next utterance after expiry starts a brand-new conversation (fresh agent
     context).
2. **Voice threads appear in WebChat as normal threads** (visible on WebChat
   load/refresh).
3. **No drift:** the conversation-creation logic is shared between the SignalR
   channel and the voice channel via code in `Domain`/`Infrastructure`.

## Decisions (from brainstorming)

- **Expiry semantics:** *Fresh thread, keep history.* On expiry, drop the in-memory
  conversation mapping so the next utterance mints a new conversation ID. The expired
  conversation's persisted Redis history and its WebChat topic are left intact.
- **WebChat integration approach:** Extract the shared conversation-creation logic
  into `Domain`/`Infrastructure`; both the SignalR channel and the voice channel call
  it. The voice channel persists the topic itself (it already holds a Redis
  connection), routed through the shared code to eliminate format drift.
- **Expiry mechanism:** Per-conversation `TimeProvider`-based `ITimer`, reset on each
  utterance; the callback cleans up on fire. Testable with `FakeTimeProvider`.
- **Live push:** On-refresh only for this work. Voice persists the topic to Redis so
  it appears whenever WebChat (re)loads its topic list. Live `OnTopicChanged` push
  (which requires reaching the SignalR hub process) is deferred to a follow-up.

## Background: how the pieces connect

- `conversationId` has the form `"{chatId}:{threadId}"`, where `chatId` and `threadId`
  are FNV-1a deterministic hashes of a GUID `topicId`
  (`McpChannelSignalR/Services/SessionService.cs`).
- The agent persists chat history to the Redis list keyed
  `agent-key:{agentId}:{chatId}:{threadId}`
  (`Infrastructure/Agents/ChatClients/RedisChatMessageStore.cs` via
  `IThreadStateStore`).
- WebChat reads that **same key** in `RedisStateService.GetHistoryAsync`
  (`McpChannelSignalR/Services/RedisStateService.cs`). Therefore, if voice mints a
  `conversationId` in the standard `{chatId}:{threadId}` format, voice turns land in
  WebChat history automatically.
- The **only** additional requirement for a thread to show in the WebChat sidebar is a
  `TopicMetadata` written to `topic:{agentId}:{chatId}:{topicId}` in Redis. This write
  is currently duplicated between `RedisThreadStateStore.SaveTopicAsync`
  (Infrastructure) and `RedisStateService.SaveTopicAsync` (SignalR); id-gen lives in
  `SessionService`. These are the drift-prone bits to centralize.
- Live `OnTopicChanged` push is SignalR-process-specific
  (`SignalRHubNotificationSender` depends on `IHubContext<ChatHub>`), so it stays in
  the SignalR channel and is out of scope here.

## Design

### 1. Shared conversation-creation (de-drift)

**`Domain/Conversations/ConversationIdentity.cs`** — record:

```
ConversationIdentity(string TopicId, long ChatId, long ThreadId, string ConversationId)
```

`ConversationId` is `"{ChatId}:{ThreadId}"`.

**`Domain/Conversations/ConversationIdGenerator.cs`** — pure id generation moved out of
`SessionService` (FNV-1a hashing with seeds `0x1234`/`0x5678`, threadId masked
positive). Produces a `ConversationIdentity` from a freshly generated `topicId`
(GUID `N` format). No external dependencies (lives in `Domain`).

**`Domain/Contracts/IConversationFactory.cs`**:

```
Task<ConversationIdentity> CreateAsync(CreateConversationParams p, CancellationToken ct = default);
```

**`Infrastructure/Conversations/ConversationFactory.cs`** — implementation:
- Generate a `ConversationIdentity` via `ConversationIdGenerator`.
- Build `TopicMetadata(topicId, chatId, threadId, agentId, topicName, createdAt,
  lastMessageAt: null, spaceSlug: "default")`.
- Persist it via the existing Redis topic-store key format
  (`topic:{agentId}:{chatId}:{topicId}`).
- Return the identity.

Topic persistence reuses the existing store contract. The implementation plan will
confirm DI registration of the topic store in both channel hosts (the SignalR host
and the voice host); if a process does not already register it, register the
Redis-backed store there. The key format and `TopicMetadata` construction live in one
place after this change.

**SignalR refactor (no behavior change):**
`SessionService.CreateConversationAsync` / `CreateConversationTool` call the shared
factory for id generation and topic persistence, then perform their SignalR-only work
(in-memory session map, `StreamService.GetOrCreateStream`, `OnTopicChanged` hub push).
The FNV id-gen is removed from `SessionService` in favor of `ConversationIdGenerator`.
Resulting Redis state is identical; existing `CreateConversationTool`/`SessionService`
tests must continue to pass.

### 2. Voice-side threading

**`McpChannelVoice/Services/VoiceConversationManager.cs`** (new singleton), keyed by
`satelliteId`, each entry holding `{ ConversationId, ITimer }`:

- `GetOrCreateAsync(satelliteId, agentId, firstUtterance, ct)`:
  - If an active conversation exists for the satellite: reset its timer
    (`ITimer.Change(lifetime, infinite)`) and return its `ConversationId`.
  - Otherwise: call `IConversationFactory.CreateAsync` with
    `CreateConversationParams { AgentId = agentId, TopicName = "{Identity} @ {Room}",
    Sender = identity, InitialPrompt = firstUtterance }`, store the mapping, start a
    `TimeProvider` timer with the configured lifetime, return the new `ConversationId`.
  - Access guarded by a lock to avoid create/renew/expire races.
- `ResolveSatelliteId(conversationId)`: reverse lookup used by the reply path; returns
  null if no active mapping.
- Timer callback (on idle expiry): remove the satellite's mapping, dispose the timer,
  and evict the satellite's `ReplyTextAccumulator` entry. Persisted history and the
  WebChat topic are not touched.

Identity/Room come from `SatelliteSession.Config` (`Identity`, `Room`).

**`SatelliteSession`**: remove the `ConversationId => SatelliteId` alias. Conversation
identity now comes from `VoiceConversationManager`, not the session. Metrics that
currently read `session.ConversationId` switch to the dynamic conversation id resolved
at the call site (or `SatelliteId` where only a satellite scope is meaningful).

**`TranscriptDispatcher.DispatchAsync`**: before emitting the
`ChannelMessageNotification`, resolve the conversation via
`VoiceConversationManager.GetOrCreateAsync(session.SatelliteId, agentId,
transcript.Text, ct)` and emit with that `conversationId`. The dropped/low-confidence
path does not create or renew a conversation (only dispatched utterances do). Approval
routing through `ApprovalCaptureBroker` stays keyed by `SatelliteId`.

**Reply path** (`SendReplyTool`, `RequestApprovalTool`): they receive `conversationId`.
Resolve the satellite via `manager.ResolveSatelliteId(conversationId)` then
`SatelliteSessionRegistry.Get(satelliteId)`. If no active mapping (e.g. a late reply
after expiry), behave as the current "session is null" path does (return `"ok"` /
`"notified"`/`"declined"`). The `ReplyTextAccumulator` keys consistently on the passed
`conversationId`.

### 3. Configuration

- Add `VoiceSettings.ConversationLifetime` (`TimeSpan`, default `00:05:00`).
- Add the corresponding key under the `Voice` section in `appsettings.json`
  (non-secret configuration — not `.env`, not a new environment variable).
- Inject `TimeProvider` into `VoiceConversationManager` (default `TimeProvider.System`)
  for testable timers.

### 4. Data flow (after change)

1. Satellite utterance → STT → `TranscriptDispatcher.DispatchAsync`.
2. Dispatcher calls `VoiceConversationManager.GetOrCreateAsync` → mints (via shared
   factory, persisting `TopicMetadata`) or renews; returns `conversationId`.
3. Dispatcher emits `ChannelMessageNotification { ConversationId = conversationId }`.
4. Agent processes the turn, persists user+assistant messages to
   `agent-key:{agentId}:{chatId}:{threadId}`, and replies via `send_reply`.
5. `SendReplyTool` resolves satellite via the manager and speaks the reply through TTS.
6. WebChat, on load/refresh, lists the topic (`topic:{agentId}:...`) and renders the
   thread's history from the shared chat-history list.
7. After 5 minutes with no further utterance, the manager's timer fires and cleans up
   the in-memory mapping. The next utterance repeats from step 2 with a new
   conversation.

## Testing (TDD, red-green-refactor)

- `ConversationIdGenerator`: deterministic ids for a given topicId; `ConversationId`
  equals `"{chatId}:{threadId}"`; threadId non-negative.
- `ConversationFactory`: persists `TopicMetadata` at `topic:{agentId}:{chatId}:{topicId}`
  with the expected fields; returns a matching `ConversationIdentity`.
- `VoiceConversationManager` (with `FakeTimeProvider`):
  - first utterance mints a conversation (factory invoked once);
  - a second utterance within the window reuses the same id and renews the timer
    (factory not invoked again);
  - advancing time past the lifetime evicts the conversation;
  - the next utterance after eviction mints a new, distinct id;
  - `ResolveSatelliteId` returns the satellite while active and null after eviction.
- `SendReplyTool` / `RequestApprovalTool`: resolve the satellite via the manager from a
  composite `conversationId`; graceful no-op when no active mapping.
- SignalR regression: existing `CreateConversationTool` / `SessionService` tests pass
  unchanged after the shared-factory refactor.

## Out of scope (follow-ups)

- Live `OnTopicChanged` push for voice topics (Redis pub/sub bridge so already-open
  WebChat views update without refresh).
- Live streaming of in-progress voice turns into an open WebChat view (voice replies go
  to TTS, not to a SignalR `StreamService` stream; history appears on refresh).

## Files touched (anticipated)

New:
- `Domain/Conversations/ConversationIdentity.cs`
- `Domain/Conversations/ConversationIdGenerator.cs`
- `Domain/Contracts/IConversationFactory.cs`
- `Infrastructure/Conversations/ConversationFactory.cs`
- `McpChannelVoice/Services/VoiceConversationManager.cs`
- Tests under `Tests/Unit/...` for the above.

Modified:
- `McpChannelVoice/Services/SatelliteSession.cs` (remove `ConversationId` alias)
- `McpChannelVoice/Services/TranscriptDispatcher.cs` (resolve conversation before emit)
- `McpChannelVoice/McpTools/SendReplyTool.cs`, `RequestApprovalTool.cs` (resolve
  satellite by conversationId)
- `McpChannelVoice/Services/ReplyTextAccumulator.cs` (key consistency, if needed)
- `McpChannelVoice/Modules/ConfigModule.cs` (register manager, factory, `TimeProvider`)
- `McpChannelVoice/Settings/VoiceSettings.cs` (`ConversationLifetime`)
- `McpChannelVoice/appsettings.json` (`Voice:ConversationLifetime`)
- `McpChannelSignalR/Services/SessionService.cs` and
  `McpChannelSignalR/McpTools/CreateConversationTool.cs` (call shared factory; drop
  inline id-gen)
- DI registration of the topic store in the relevant channel host(s) as needed.
