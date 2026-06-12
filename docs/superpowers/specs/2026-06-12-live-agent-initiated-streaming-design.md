# Live Streaming of Agent-Initiated Replies into Existing WebChat Conversations

**Date:** 2026-06-12
**Status:** Approved

## Problem

When the library MCP server fires a download-completion alert (`channel/message` with
`Origin = Download`, `ReplyTo = [origin conversation]`), the agent's reply is delivered
to the originating WebChat conversation via `send_reply` and persisted to the Redis
thread — but a user currently viewing that conversation sees nothing until they refresh.
The same gap affects every agent-initiated reply into an *existing* WebChat conversation
(schedule results delivered to an existing conversation included).

## Root Cause

Both missing pieces live server-side in `McpChannelSignalR`:

1. `StreamService.WriteMessageAsync` drops reply chunks when no in-memory stream exists
   for the topic. Streams are only created by user-initiated `ChatHub.SendMessage` or by
   `create_conversation` when a *new* conversation is minted. An agent-initiated turn
   into an existing conversation has neither, so its chunks are dropped with a warning.
2. `OnStreamChanged` has zero server-side emitters. The WebChat client fully handles it
   (`SignalREventSubscriber` subscribes; `HubEventDispatcher.HandleStreamChanged` on
   `Started` calls `StreamResumeService.TryResumeStreamAsync`, which replays the buffer
   and subscribes live) — the handler just never fires.

The new-conversation flow works live precisely because `create_conversation` sets up
session + seeded stream + hub broadcast *before* reply chunks arrive. The
existing-conversation flow lacks that turn-start moment.

## Decisions

- **Generic fix**: applies to all agent-initiated replies into existing WebChat
  conversations, not just library download alerts.
- **Mirror refresh**: the live view shows the originating notification text as a
  user-role bubble (exactly what a refresh renders from the persisted thread), then the
  agent reply streams under it.
- **Approach**: turn-start announce via `create_conversation`'s existing
  `existingConversationId` attach parameter. No wire-protocol changes, no client changes.

## Architecture

Flow for a download alert (steps 1 and 4–5 unchanged today):

1. Library emits `channel/message` with `Origin = Download` and
   `ReplyTo = [origin conversation]`.
2. ChatMonitor resolves delivery targets. **New:** for each agent-initiated message
   (`message.Origin is not null`), before streaming the reply, it announces turn start by
   calling `CreateConversationAsync(agentId, topicName, sender,
   initialPrompt: message.Content, address, existingConversationId: target.ConversationId)`
   on each delivery target, except:
   - targets minted by this same resolution (their `create_conversation` already ran —
     announcing again would double-increment the pending count and wedge the stream open);
   - attach-only voice targets (see Edge Cases).
3. **New:** the SignalR channel's `CreateConversationTool` attach branch (when
   `existingConversationId` is provided):
   - Resolve topicId via `SessionService.GetTopicIdByConversationId`. No session →
     return `existingConversationId` with no side effects (graceful no-op).
   - Ensure the stream exists, mirroring the `SendMessage`/`EnqueueMessage` idiom:
     `GetOrCreateStream(topicId, currentPrompt: initialPrompt, sender)`,
     `TryIncrementPending`, and write the user-role bubble
     (`ChatStreamMessage { Content, UserMessage = UserMessageInfo(sender, now) }`)
     into the buffer.
   - Broadcast `OnStreamChanged(Started, topicId)` to the session's space group via
     `IHubNotificationSender`.
   - Return `existingConversationId`.
4. `send_reply` chunks land in the existing stream (buffered + broadcast to live
   subscribers). Viewing clients received `Started`, auto-resumed, and render the user
   bubble plus the streaming reply.
5. `StreamComplete` → `CompleteStream` → teardown + existing push notification.

## Component Changes

| Component | Change |
|-----------|--------|
| `Domain/Monitor/ChatMonitor.cs` | Per-message turn-start announce in the default processing branch (after `messageTargets` resolution, before the agent run). |
| `Domain/Monitor/ChatMonitor.cs` (`DeliveryTarget`) | Add a `Minted` flag so just-created conversations are not announced twice. Targets reused from the group-level resolution by later messages count as pre-existing for those messages. |
| `McpChannelSignalR/McpTools/CreateConversationTool.cs` | Attach branch as described above. |
| `McpChannelSignalR/Services/StreamService.cs` | Small seam if needed to expose "ensure stream + seed + pending" as one operation for the attach branch. |
| WebChat.Client | **No changes.** |
| Other channel servers | **No changes.** Telegram/ServiceBus lack `create_conversation`; `McpChannelConnection.CreateConversationAsync` already checks tool presence and returns null gracefully. Voice is explicitly skipped. |

## Edge Cases & Error Handling

- **Nobody looking / channel server restarted**: no in-memory session → attach no-ops;
  behavior degrades to today's (persisted, visible on refresh). When someone is viewing,
  the session always exists (`TopicSelectionEffect` calls `StartSession` on topic open).
- **Voice targets skipped**: voice's attach branch (`VoiceDeliveryRegistry.Bind`) is
  built for schedule fan-out; a stray binding can expire mid-turn and flush the shared
  `ReplyTextAccumulator`, dropping spoken reply text. ChatMonitor already discriminates
  voice targets by `ChannelProtocol.VoiceChannelId` for ordering; the announce reuses
  that discriminator to skip them. Voice delivery of download alerts keeps today's
  behavior (spoken when the satellite session is alive, dropped otherwise).
- **Announce failure**: `CreateConversationAsync` returns null on tool absence or
  `McpException`; the delivery target is kept regardless (its conversation id is already
  known). Worst case is today's refresh-only behavior — never a lost reply.
- **Concurrent user turn in the same conversation**: attach joins the existing stream
  (`GetOrCreateStream` is idempotent) and increments pending, mirroring
  `EnqueueMessage`; each turn's `StreamComplete` decrements once, so teardown balances.
- **Push notifications**: unchanged — `CompleteStream` already fires them today even
  without a live stream; they now coexist with live delivery.
- **`OnStreamChanged(Completed/Cancelled)` broadcasts**: considered and dropped. Every
  client that reacts to `Started` resumes the stream and learns completion from its own
  subscription ending; an extra broadcast adds no information.

## Out of Scope

- Live voice announcement of download alerts (separate feature; night-time UX
  implications).
- Sidebar topic reordering / `LastMessageAt` metadata for agent-initiated turns
  (stale today after refresh too; cosmetic).
- Redis-backed session recovery after a channel-server restart (only matters when nobody
  is looking, where persisted-only behavior is acceptable).

## Testing

TDD (red-green) per project rules.

- **ChatMonitor units**: announce fires for `Origin`-set messages with pre-existing
  conversation targets; not for user-interactive messages (`Origin` null); not for
  just-minted targets; not for voice targets; announce failure does not drop reply
  delivery.
- **SignalR channel units**: attach with a session → stream seeded with prompt/sender,
  pending incremented, user bubble buffered, `OnStreamChanged(Started)` broadcast to the
  space group, same conversation id returned; without a session → pure no-op returning
  the id; with an already-active stream → joins without duplicate setup. Existing
  create-path (`existingConversationId == null`) tests stay green.
- **E2E (`Tests/E2E/WebChat`)**: with a topic open, inject a library-style
  agent-initiated reply → assert it appears without refresh; assert a refresh renders
  the identical conversation (user bubble + agent reply).
