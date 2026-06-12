# Library Download Alerts via Channel Protocol — Design

**Date:** 2026-06-12
**Branch:** `subscription-refactor`
**Status:** Approved

## Problem

Download-finished alerts ride on MCP resource subscriptions today. `SubscriptionMonitor`
(McpServerLibrary) polls qBittorrent per subscribed `(sessionId, uri)` and emits
`notifications/resources/updated`; on the agent side `McpSubscriptionManager`,
`ResourceUpdateProcessor`, and `McpResourceManager` read the resource, run the agent with a
synthetic message, and push updates into a `SubscriptionChannel` that
`McpAgent.RunCoreStreamingInnerAsync` merges with the live response stream.

Routing works only because the subscription lives on a conversation-scoped MCP session. That
makes it fragile: subscriptions die with the session (hence `resubscribe_downloads`), the
stream merge complicates `McpAgent`, and the merge blocks per-conversation turn serialization
(see memory: message pipeline constraints). The library server is the **only** emitter of
resource-update notifications, so the whole agent-side machinery exists for this one feature.

## Approach

McpServerLibrary becomes a **dual-role** server (channel + filesystem + tools), the same shape
as `McpServerScheduling`. Download completion is pushed as a `channel/message` with `replyTo`
targeting the originating conversation; `ChatMonitor` already restores threads by
conversationId and delivers replies to `replyTo` targets. The agent-side resource-subscription
stack is deleted entirely.

Decisions made during brainstorming:

1. **Routing capture:** per-tool-call context injection by the agent infrastructure (not
   LLM-passed, not per-session registration).
2. **Wire format:** MCP `RequestParams.Meta` on `tools/call` (not a hidden argument).
3. **Tool surface:** downloads move to the VFS idiom (`filesystem://downloads`), replacing the
   status/cleanup tools and `download://` resources.
4. **Persistence:** downloadId → routing snapshots persist in Redis (already in the stack), so
   completions survive library-server restarts.

## 1. Dual-role McpServerLibrary

- **`DownloadCompletionWatcher`** (BackgroundService, replaces `SubscriptionMonitor`): polls
  `IDownloadClient` for every entry in the routing store — global, no per-session
  subscriptions. On `DownloadState.Completed` it emits the completion `channel/message` and
  deletes the routing entry **only after successful emission**: if no agent session is
  connected, the entry is retained and retried on the next poll (durable until delivered).
- **`DownloadNotificationEmitter`**: active-session registry plus
  `ChannelProtocol.MessageNotification` emission, mirroring `ScheduleNotificationEmitter`.
  The `JsonSerializerOptions` must carry a `TypeInfoResolver` (the SDK calls `MakeReadOnly()`;
  see memory: MCP SendNotificationAsync MakeReadOnly). The duplication with scheduling is
  small; the implementation plan may extract a shared emitter helper into Infrastructure if it
  stays mechanical — optional, not a goal.
- Channel-protocol surface (`send_reply` = ack/no-op, `request_approval`,
  `create_conversation`, `register_agents`) implemented as in scheduling; hidden from the LLM
  by the existing dual-role mechanism.
- Agent config: the library server is added to `ChannelEndpoints` while remaining in
  `mcpServerEndpoints`.

## 2. Routing snapshot via `_meta`

- New DTO `ConversationContext { AgentId, ConversationId, UserId, Origin: ReplyTarget }` in
  `Domain/DTOs/Channel`.
- The current turn's context flows from `ChatMonitor` into the agent run; the agent-side MCP
  tool wrapper attaches it as `RequestParams.Meta` on **every** `tools/call`. Uniform
  injection means future dual-role servers get routing context for free; servers that don't
  read Meta ignore it. The LLM never sees it; it does not appear in tool schemas or persisted
  chat history.
- Exact plumbing (run-options `AdditionalProperties` read via
  `FunctionInvokingChatClient.CurrentContext`, vs. a per-turn slot on `ThreadSession`) gets a
  verification spike at the start of the implementation plan — it depends on the pinned MCP
  SDK and Microsoft.Extensions.AI surfaces. Note the SDK's convenience `CallToolAsync` may not
  expose Meta; the wrapper may need to send the request directly.
- `McpFileDownloadTool` reads Meta from the request context and stores
  `downloadId → ConversationContext` in **`IDownloadRoutingStore`** (Redis, TTL ~60 days —
  replaces `ITrackedDownloadsManager`). If the store write fails, the tool call returns an
  error result: a download whose completion can never be announced should not start silently.

## 3. The completion message

`ChannelMessageNotification` fields:

| Field | Value |
|-------|-------|
| `conversationId` | The originating conversation — thread restores from Redis, so the agent has full context of why it downloaded |
| `agentId` | From the routing snapshot |
| `sender` | The original user |
| `origin` | `library` |
| `content` | Templated prompt: "Download 'Title' (id 42) has finished downloading to \<savePath\>…" |
| `replyTo` | `[Origin ReplyTarget with the concrete conversationId]` |

`ChatMonitor` consumes this through the existing schedule-shaped path. A concrete
conversationId in a `ReplyTarget` must skip conversation minting — verify and pin with a test.
Voice-origin downloads carry the voice target (with satellite address) in `replyTo` and
announce back on the satellite; since the conversation already exists there is no
minting/anchoring concern (the `targets[0]` non-voice invariant applies to minting shared
conversations). Gets an explicit test.

Only `Completed` triggers a message (today's semantics). Failure/stall alerts are a possible
follow-up, not in scope; the routing-store TTL bounds leakage from never-completing torrents.

## 4. `filesystem://downloads` VFS

- `DownloadsFileSystem` in `Domain/Tools/Downloads/Vfs` implementing `IFileSystemBackend` with
  typed `FsResult<T>`, mounted at `/downloads`. Second filesystem resource from the library
  server, next to `filesystem://media` — `McpFileSystemDiscovery` mounts every `filesystem://`
  resource; multi-resource-per-server is verified in the plan.
- Layout: `/downloads/<id>/status.json` — read-only
  `{id, title, state, progress, size, eta, savePath}` straight from `IDownloadClient`
  (global qBittorrent view; no session scoping, unlike today's tracked-downloads listing).
- `fs_delete /downloads/<id>` → cleanup with today's `CleanupDownloadTool` semantics, plus
  removal of the routing entry. `move`, `exec`, and file creation are unsupported
  (printer-style envelopes).
- The library MCP prompt teaches the `/downloads` idiom and drops mentions of the removed
  status/cleanup/resubscribe tools.

## 5. Removals

**Agent side (the big win):**

- `Infrastructure/Agents/Mcp/McpSubscriptionManager.cs`
- `Infrastructure/Agents/Mcp/ResourceUpdateProcessor.cs`
- `Infrastructure/Agents/Mcp/McpResourceManager.cs`
- The `SubscriptionChannel` `Merge` + post-stream drain in
  `McpAgent.RunCoreStreamingInnerAsync` — the method collapses to the plain inner stream
- `enableResourceSubscriptions` plumbing through `ThreadSession` / `MultiAgentFactory`

Side effect: unblocks the deferred per-conversation turn serialization (out of scope here).

**Library side:**

- `ResourceSubscriptions/SubscriptionMonitor.cs`, `SubscriptionTracker.cs`,
  `SubscriptionHandlers.cs`
- `McpResources/McpDownloadResource.cs` + `Domain/Resources/DownloadResource.cs`
- `McpGetDownloadStatusTool`, `McpCleanupDownloadTool`, `McpResubscribeDownloadsTool` and
  their Domain counterparts (`GetDownloadStatusTool`, `CleanupDownloadTool`,
  `ResubscribeDownloadsTool`)
- `ITrackedDownloadsManager` / `TrackedDownloadsManager` (replaced by `IDownloadRoutingStore`)

**Kept:** `file_search`, `download_file` (+ `SearchResultsManager`, still session-keyed),
`filesystem://media`.

## 6. Config / infrastructure (same change, per the env-var rule)

- `DockerCompose/docker-compose.yml`: `mcp-library` gains the Redis connection environment
  variable and `depends_on: redis`.
- `appsettings.json` / `appsettings.Development.json`: Redis key for the library server
  (non-secret, in-cluster — no `.env` entry).
- Agent `ChannelEndpoints` gains the library entry (dual-role).

## 7. Error handling

- **No live agent session at completion:** retain the routing entry, retry next poll.
- **Routing-store write failure at download time:** fail the `download_file` call.
- **Torrent vanished (item null):** drop the routing entry silently; `status.json` 404s
  (today's semantics).
- **Redis unavailable in the watcher:** log and retry next poll; entries are not lost.

## 8. Testing (TDD throughout)

- `DownloadsFileSystem` contract tests mirroring the `ScheduleFileSystem` /
  `PrinterQueueFileSystem` suites.
- `RedisDownloadRoutingStore` tests following existing Redis test patterns.
- `DownloadCompletionWatcher`: completion → emit + delete entry; no active session → retain +
  retry; vanished torrent → silent drop; emission failure → entry retained.
- `_meta` injection: wrapper attaches `ConversationContext`; `McpFileDownloadTool` extracts it.
- `ChatMonitor`: concrete-conversationId `replyTo` skips minting; voice-origin delivery.
- Codec/protocol: completion notification round-trips through `ChannelProtocol` serialization.
