# Azure Service Bus Chat Messenger Design

## Overview

Add Azure Service Bus queue monitoring as a new chat messenger, enabling external systems (CI/CD pipelines, scheduled jobs, other services) to trigger agent work via queue messages. Responses stream to WebChat in real-time AND are sent back to a response queue.

## Architecture

### Components

**ServiceBusChatMessengerClient** - Standalone `IChatMessengerClient` implementation:
- Reads prompts from Azure Service Bus queue
- Manages its own topic/thread state (maps `sourceId` to `chatId`/`threadId`)
- Sends responses back to a response queue via `ProcessResponseStreamAsync()`
- Has no knowledge of WebChat internals

**CompositeChatMessengerClient** - Combines multiple clients:
- Merges `ReadPrompts()` from all registered clients into one stream
- Broadcasts `ProcessResponseStreamAsync()` to all clients (so WebChat sees Service Bus responses)
- Delegates `CreateTopicIfNeededAsync()` to the appropriate client based on source

### DI Registration

When `ChatInterface.Web` is selected AND Service Bus is configured:
- Register `WebChatMessengerClient`
- Register `ServiceBusChatMessengerClient`
- Register `CompositeChatMessengerClient` as `IChatMessengerClient` (wrapping both)

## Message Format

### Incoming Message (Service Bus → Agent)

**Queue:** `agent-prompts`

**Body (JSON):**
```json
{
  "prompt": "Analyze the latest deployment logs",
  "sender": "ci-pipeline-main"
}
```

**Message Properties** (Azure Service Bus application properties):
- `sourceId` (required): Groups messages into conversations (e.g., `"cicd-main-pipeline"`)
- `agentId` (optional): Target agent ID; if omitted, uses default agent

**Routing Logic:**
1. First message with a new `sourceId` creates a new topic (`chatId` + `threadId`)
2. Subsequent messages with the same `sourceId` continue the conversation
3. The `sourceId` → `chatId`/`threadId` mapping is persisted in Redis

### Outgoing Message (Agent → Response Queue)

**Queue:** `agent-responses`

**Body (JSON):**
```json
{
  "sourceId": "cicd-main-pipeline",
  "response": "Based on the logs, I found 3 warnings...",
  "agentId": "default",
  "completedAt": "2026-02-03T14:30:00Z"
}
```

## Implementation

### New Files

```
Infrastructure/Clients/Messaging/
├── ServiceBusChatMessengerClient.cs   # IChatMessengerClient implementation
├── ServiceBusResponseWriter.cs        # Sends responses to response queue
├── ServiceBusSourceMapper.cs          # Maps sourceId → chatId/threadId (Redis-backed)
└── CompositeChatMessengerClient.cs    # Merges multiple IChatMessengerClient instances

Domain/DTOs/
└── ServiceBusPromptMessage.cs         # DTO for incoming queue messages

Agent/Settings/
└── ServiceBusSettings.cs              # Configuration POCO
```

### CompositeChatMessengerClient

```csharp
public class CompositeChatMessengerClient(
    IReadOnlyList<IChatMessengerClient> clients) : IChatMessengerClient
{
    public bool SupportsScheduledNotifications =>
        clients.Any(c => c.SupportsScheduledNotifications);

    public IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken ct)
        => clients.Select(c => c.ReadPrompts(timeout, ct)).Merge(ct);

    public async Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
        CancellationToken ct)
    {
        // Broadcast to all clients - each handles responses relevant to it
        var broadcast = updates.Broadcast(clients.Count);
        await Task.WhenAll(clients.Select((c, i) =>
            c.ProcessResponseStreamAsync(broadcast[i], ct)));
    }
    // ... delegate other methods based on agentId/chatId routing
}
```

### ServiceBusChatMessengerClient

- Uses `Azure.Messaging.ServiceBus` SDK
- `ServiceBusProcessor` for receiving with auto-complete disabled
- Completes messages only after `ChatPrompt` is successfully enqueued
- Dead-letters malformed messages

## Error Handling

| Scenario | Handling |
|----------|----------|
| Malformed message (missing prompt) | Dead-letter with reason, log warning |
| Missing `sourceId` property | Generate UUID as sourceId (one-off conversation) |
| Agent not found for `agentId` | Use default agent, log warning |
| Response queue send failure | Log error, don't block prompt processing |
| Service Bus connection lost | SDK handles reconnection; log and continue |

## WebChat Synchronization

Since `CompositeChatMessengerClient` broadcasts responses to all clients:

1. **ServiceBusChatMessengerClient** receives a prompt with `sourceId`
2. It calls `CreateTopicIfNeededAsync()` which creates a topic in `IThreadStateStore` (Redis)
3. The prompt flows through `ChatMonitor` to the agent
4. Responses are broadcast to both clients via `ProcessResponseStreamAsync()`
5. **WebChatMessengerClient** sees the response, looks up the topic by `chatId`, and streams to SignalR

**Result:** Users browsing WebChat see Service Bus conversations appear as regular topics with streaming responses.

### Topic Naming

Service Bus topics are named: `"[SB] {sourceId}"` (e.g., `"[SB] cicd-main-pipeline"`)

## Testing Strategy

**Unit Tests:**
- `CompositeChatMessengerClient` merges prompts correctly
- `CompositeChatMessengerClient` broadcasts responses to all clients
- `ServiceBusSourceMapper` correctly maps sourceId → topic
- Message deserialization handles edge cases

**Integration Tests:**
- End-to-end: send message to queue → receive response on response queue
- Topic persistence: same sourceId reuses existing conversation
- Use Azure Service Bus emulator or test queue

## Configuration

```json
{
  "serviceBus": {
    "connectionString": "Endpoint=sb://...",
    "promptQueueName": "agent-prompts",
    "responseQueueName": "agent-responses",
    "maxConcurrentCalls": 10
  }
}
```
