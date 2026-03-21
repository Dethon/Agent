# MCP Channel Protocol Design

## Problem

The agent currently compiles all transport logic (Telegram, WebChat/SignalR, ServiceBus, CLI) into the main process. Each transport implements `IChatMessengerClient`, and `CompositeChatMessengerClient` + `MessageSourceRouter` multiplex them. This couples the agent core to every transport, requires recompilation to add new ones, and spreads transport-specific concerns across Domain, Infrastructure, and Agent projects.

## Goal

Extract each transport into an independent MCP server ("channel"). The agent core sees a single, unified message flow. New transports can be added by deploying a new channel MCP server — no agent changes needed.

## Architecture

### Channel MCP Server Protocol

Each channel is a standard MCP server (HTTP/SSE transport, Docker container) that implements a fixed protocol surface:

#### Inbound: `channel/message` notification (Channel → Agent)

```json
{
  "method": "notifications/channel/message",
  "params": {
    "conversationId": "string",
    "sender": "string",
    "content": "string",
    "agentId": "string | null"
  }
}
```

- `conversationId` is opaque to the agent. The channel maps it internally to platform-native concepts (Telegram chatId + forumTopicId, WebChat space + thread, ServiceBus correlationId).
- The channel owns all thread/topic lifecycle. The agent never creates, lists, or manages threads.

#### Outbound: `send_reply` tool (Agent → Channel)

```json
{
  "name": "send_reply",
  "params": {
    "conversationId": "string",
    "content": "string",
    "contentType": "text | reasoning | tool_call | error | stream_complete",
    "isComplete": "bool"
  }
}
```

- Called per response chunk during streaming. `isComplete: true` signals the final chunk.
- `contentType` distinguishes different kinds of response content (see content type mapping below).
- The channel handles platform-specific formatting, message splitting, typing indicators, etc.

#### Outbound: `request_approval` tool (Agent → Channel)

```json
{
  "name": "request_approval",
  "params": {
    "conversationId": "string",
    "mode": "request | notify",
    "requests": [{ "toolName": "string", "arguments": "object" }]
  },
  "returns": {
    "result": "approved | denied | approved_and_remember",
    "toolNames": ["string"]
  }
}
```

- `mode: "request"` — the channel renders approval UI appropriate to its platform (inline keyboard, dialog, auto-approve). Blocks until the user responds or a timeout occurs.
- `mode: "notify"` — fire-and-forget notification that a tool was auto-approved (whitelisted or dynamically remembered). The channel displays this to the user but does not wait for a response. This covers the current `NotifyAutoApprovedAsync` behavior.

#### Content type mapping

The agent-side mapping from `AgentResponseUpdate` to `send_reply` calls:

| `AgentResponseUpdate` content | `contentType` | `content` value |
|-------------------------------|---------------|-----------------|
| `TextContent` | `text` | The text chunk |
| `TextReasoningContent` | `reasoning` | The reasoning text |
| `FunctionCallContent` | `tool_call` | JSON-serialized tool name + arguments |
| Error text from `WithErrorHandling()` | `error` | Error message |
| `StreamCompleteContent` | `stream_complete` | Empty (signals end of response) |

Each channel decides how to render each content type. For example, Telegram may ignore `reasoning`, WebChat may show it in a collapsible panel, ServiceBus may include it in the response payload.

### ChatMonitor Refactoring

`ChatMonitor` becomes the channel multiplexer, replacing `CompositeChatMessengerClient` and `MessageSourceRouter`.

```csharp
public class ChatMonitor(
    IReadOnlyList<IChannelConnection> channels,
    IAgentFactory agentFactory,
    ChatThreadResolver threadResolver,
    ILogger<ChatMonitor> logger)
```

`IChannelConnection` wraps an MCP client connected to one channel server. This interface lives in Domain — it contains no MCP-specific types, only domain primitives (`string`, `ChannelMessage`, `ToolApprovalRequest`). The MCP transport details are encapsulated in the Infrastructure implementation (`McpChannelConnection`).

```csharp
public interface IChannelConnection
{
    string ChannelId { get; }
    IAsyncEnumerable<ChannelMessage> Messages { get; }
    Task SendReplyAsync(string conversationId, string content, string contentType, bool isComplete, CancellationToken ct);
    Task<ToolApprovalResult> RequestApprovalAsync(string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct);
    Task NotifyAutoApprovedAsync(string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct);
}
```

#### Tool Approval Wiring

`IToolApprovalHandlerFactory` is replaced by a single `ChannelToolApprovalHandler` that receives the originating `IChannelConnection` and `conversationId` at construction time. `MultiAgentFactory` creates one per agent, passing the channel that originated the conversation:

```csharp
public class ChannelToolApprovalHandler(IChannelConnection channel, string conversationId) : IToolApprovalHandler
{
    public Task<ToolApprovalResult> RequestApprovalAsync(IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct)
        => channel.RequestApprovalAsync(conversationId, requests, ct);

    public Task NotifyAutoApprovedAsync(IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct)
        => channel.NotifyAutoApprovedAsync(conversationId, requests, ct);
}
```

This means `ProcessChatThread` must track which channel originated each conversation and pass it through to agent creation.

`Monitor()` merges messages from all channels:

```csharp
public async Task Monitor(CancellationToken ct)
{
    var allMessages = channels
        .Select(ch => ch.Messages.Select(m => (Channel: ch, Message: m)))
        .Merge(ct);

    var responses = allMessages
        .GroupByStreaming(x => ToAgentKey(x.Message), ct)
        .Select(group => ProcessChatThread(group, ct))
        .Merge(ct);

    await foreach (var (channel, conversationId, content, contentType, isComplete) in responses.WithCancellation(ct))
        await channel.SendReplyAsync(conversationId, content, contentType, isComplete, ct);
}
```

### Simplified Domain Types

**`ChatPrompt` → `ChannelMessage`**

```csharp
public record ChannelMessage
{
    public required string ConversationId { get; init; }
    public required string Content { get; init; }
    public required string Sender { get; init; }
    public required string ChannelId { get; init; }
    public string? AgentId { get; init; }
}
```

**`AgentKey` simplification**

```csharp
public readonly record struct AgentKey(string ConversationId, string? AgentId = null);
```

The channel already resolved the conversation identity — `ChatId` + `ThreadId` collapse into a single opaque `ConversationId`.

**`MessageSource` enum** — removed entirely. Replaced by `ChannelId` (string) on `ChannelMessage` and `IChannelConnection`.

### ScheduleExecutor Migration

`ScheduleExecutor` currently depends on `IChatMessengerClient` to create topics, start scheduled streams, and push responses. In the channel architecture:

- `ScheduleExecutor` receives `IReadOnlyList<IChannelConnection>` instead of `IChatMessengerClient`
- It targets a configurable default channel for scheduled output (e.g., the SignalR channel, since `SupportsScheduledNotifications` was only true for WebChat)
- **Topic creation**: The channel must support agent-initiated conversations. Add a `create_conversation` tool to channels that support scheduling:

```json
{
  "name": "create_conversation",
  "params": {
    "agentId": "string",
    "topicName": "string",
    "sender": "string"
  },
  "returns": {
    "conversationId": "string"
  }
}
```

- Only channels that support scheduling expose this tool. Others (ServiceBus, Telegram) do not.
- The `SupportsScheduledNotifications` check becomes: "does the target channel expose `create_conversation`?" — discoverable via MCP tool listing.
- If multiple channels expose `create_conversation`, the schedule's target channel is determined by configuration (default channel ID in appsettings). Future enhancement: allow per-schedule channel targeting.
- After creating a conversation, `ScheduleExecutor` uses `send_reply` to push response chunks, same as `ChatMonitor`.

### Redis Key Migration

Changing `AgentKey` from `(long ChatId, long ThreadId, string? AgentId)` to `(string ConversationId, string? AgentId)` changes the Redis key format from `agent-key:{AgentId}:{ChatId}:{ThreadId}` to `agent-key:{AgentId}:{ConversationId}`.

Migration strategy: **dual-read fallback** in `RedisThreadStateStore`. On session restore, if the new key format yields no result, attempt a lookup with the legacy format. Each channel defines how it constructs `conversationId` from the old `ChatId`/`ThreadId` pair, making reverse mapping possible. This allows gradual migration without data loss. Legacy fallback can be removed after one TTL cycle (30 days).

## Channel Server Implementations

### McpChannelSignalR

- Hosts the SignalR hub (moved from Agent project)
- Manages WebChat connections, spaces, topics
- Receives messages from SignalR clients → emits `channel/message` notification
- `send_reply` → pushes response chunks through SignalR to clients
- `request_approval` → sends approval dialog to WebChat UI, waits for response
- Absorbs: `HubNotifier`, `WebChatStreamManager`, `WebChatSessionManager`, `ChatHub`, space/topic lifecycle

### McpChannelTelegram

- Runs Telegram bot polling
- Manages forum topics, sender allowlists
- Receives Telegram messages → emits `channel/message` notification
- `send_reply` → sends via Telegram Bot API (formatting, message splitting)
- `request_approval` → sends inline keyboard buttons, waits for callback
- Absorbs: `TelegramChatClient` logic, forum topic creation, message formatting

### McpChannelServiceBus

- Connects to Azure Service Bus
- Receives from prompt queue → emits `channel/message` notification
- `send_reply` → sends to response queue with correlation ID
- `request_approval` → auto-approves (or routes through a separate approval queue)
- Absorbs: `ServiceBusProcessor`, `ServiceBusChatMessengerClient`, correlation tracking

### Deployment

Each channel is a Docker container. Agent connects via HTTP/SSE transport (same as existing MCP servers). Configuration in `docker-compose.yml` follows the existing `mcp-*` pattern.

## What Gets Removed

### From Domain
- `IChatMessengerClient` interface
- `IToolApprovalHandlerFactory` interface
- `MessageSource` enum
- `ChatPrompt` record (replaced by `ChannelMessage`)
- `ChatPrompt.ChatId`, `ThreadId`, `MessageId` fields

### From Infrastructure
- `CompositeChatMessengerClient`
- `MessageSourceRouter`
- `WebChatMessengerClient`, `TelegramChatClient`, `ServiceBusChatMessengerClient`, `CliChatMessengerClient`, `OneShotChatMessengerClient`
- All `*ToolApprovalHandlerFactory` implementations
- `HubNotifier`, `WebChatStreamManager`, `WebChatSessionManager` (move to McpChannelSignalR)

### From Agent project
- `ChatHub` (moves to McpChannelSignalR)
- Transport wiring in `InjectorModule` (replaced by channel connection configuration)
- SignalR hosting
- CLI transport (removed entirely, was unused)

## What Gets Added

### Domain
- `IChannelConnection` interface
- `ChannelMessage` record

### Infrastructure
- `McpChannelConnection` — implements `IChannelConnection` using MCP .NET client; handles notification subscription and tool invocation

### New projects
- `McpChannelSignalR` — SignalR/WebChat channel server
- `McpChannelTelegram` — Telegram channel server
- `McpChannelServiceBus` — Azure Service Bus channel server

## Error Handling

### Channel disconnection
- `IChannelConnection` detects SSE connection drop
- Queues responses temporarily (bounded buffer)
- Reconnects with backoff
- If a conversation was mid-stream, resends the last incomplete response on reconnect
- ChatMonitor continues processing other channels unaffected

### Notification delivery
- `channel/message` notifications are fire-and-forget from the channel's perspective
- If the agent isn't connected, the channel buffers messages (bounded) and emits on reconnect
- No at-least-once delivery guarantees — users can resend on brief disconnects

### Tool call failures
- `send_reply` failure → log warning, skip that chunk (channel shows partial response)
- `request_approval` failure → treat as denied (safe default), notify agent

## Testing

### Unit tests
- `ChatMonitor` — merges streams from multiple `IChannelConnection` mocks, routes responses to originating channel
- `McpChannelConnection` — notification-to-`ChannelMessage` mapping, tool call serialization
- Each channel server's tools — `send_reply` and `request_approval` logic in isolation

### Integration tests
- Channel ↔ agent: spin up a channel server, connect via MCP, send notification, verify agent calls `send_reply`
- Multi-channel: connect two channels simultaneously, verify correct routing
- Reconnection: drop and restore a channel connection, verify buffering and resume

### Migration from existing tests
- `ToolApprovalChatClient` tests stay (client still exists, handler factory changes)
- Telegram/WebChat-specific tests move to respective channel server projects
- `CompositeChatMessengerClient` tests deleted (replaced by ChatMonitor multiplexing tests)

## Design Decisions

- **`IChannelConnection` in Domain**: The interface uses only domain primitives (strings, records). MCP-specific types are confined to the `McpChannelConnection` implementation in Infrastructure. This follows the existing pattern where Domain defines transport-agnostic interfaces.
- **`MessageId` dropped from `ChannelMessage`**: Platform-specific message IDs (used for reply-to threading in Telegram) are now internal to the channel. The channel tracks them as needed.
- **Bounded buffer sizes**: Configurable via environment variables / appsettings, following existing configuration conventions. Defaults TBD during implementation.

## Non-Goals

- CLI transport: removed entirely (was unused)
- OneShot mode: removed with CLI
