# Per-Prompt Response Routing Design

## Problem Statement

The `CompositeChatMessengerClient` currently broadcasts ALL responses to ALL clients. This is incorrect behavior:

- **Current**: Service Bus prompt -> response goes to WebUI AND Service Bus
- **Current**: WebUI prompt -> response goes to WebUI AND Service Bus (wrong!)
- **Expected**: WebUI prompt -> response goes ONLY to WebUI

The WebUI should act as a "universal viewer" that sees all messages, but Service Bus should only receive responses for prompts that originated from Service Bus.

## Requirements

1. Track the **source of each prompt** (per-prompt, not per-session)
2. Route responses based on prompt origin:
   - **WebUI**: Receives ALL responses (universal viewer)
   - **ServiceBus**: Only receives responses for Service Bus-originated prompts
   - **Telegram**: Only receives responses for Telegram-originated prompts
3. A chat session may receive prompts from multiple sources over time; each response routes according to its specific prompt's origin

## Solution Design

### 1. New Enum: `MessageSource`

**File**: `Domain/DTOs/MessageSource.cs`

```csharp
namespace Domain.DTOs;

public enum MessageSource
{
    WebUi,
    ServiceBus,
    Telegram
}
```

### 2. Extend `IChatMessengerClient` Interface

**File**: `Domain/Contracts/IChatMessengerClient.cs`

Add property to identify each client's source type:

```csharp
MessageSource Source { get; }
```

### 3. Extend `ChatPrompt` DTO

**File**: `Domain/DTOs/ChatPrompt.cs`

Add property to track prompt origin:

```csharp
public MessageSource Source { get; init; }
```

### 4. Update Concrete Clients

Each client implements the `Source` property and sets it on prompts:

**TelegramChatMessengerClient**:
```csharp
public MessageSource Source => MessageSource.Telegram;
```

**ServiceBusChatMessengerClient**:
```csharp
public MessageSource Source => MessageSource.ServiceBus;
```

**WebUI client** (SignalR-based):
```csharp
public MessageSource Source => MessageSource.WebUi;
```

Each client sets `Source` when creating `ChatPrompt` instances.

### 5. Update `CompositeChatMessengerClient`

**File**: `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs`

#### 5.1 Add Source Tracking

```csharp
private readonly ConcurrentDictionary<long, MessageSource> _chatIdToSource = new();
```

#### 5.2 Update `ReadPrompts` to Track Sources

When merging prompts from clients, store the source mapping:

```csharp
public async IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken ct)
{
    await foreach (var prompt in clients
        .Select(c => c.ReadPrompts(timeout, ct))
        .Merge(ct))
    {
        _chatIdToSource[prompt.ChatId] = prompt.Source;
        yield return prompt;
    }
}
```

#### 5.3 Update `ProcessResponseStreamAsync` with Routing

Change signature to pair clients with channels:

```csharp
public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
    CancellationToken cancellationToken)
{
    Validate();

    var clientChannels = clients
        .Select(client => (
            client,
            channel: Channel.CreateUnbounded<(AgentKey, AgentResponseUpdate, AiResponse?)>()))
        .ToArray();

    var broadcastTask = BroadcastUpdatesAsync(updates, clientChannels, cancellationToken);

    var processTasks = clientChannels
        .Select(pair => pair.client.ProcessResponseStreamAsync(
            pair.channel.Reader.ReadAllAsync(cancellationToken),
            cancellationToken))
        .ToArray();

    await broadcastTask;
    await Task.WhenAll(processTasks);
}
```

#### 5.4 Update `BroadcastUpdatesAsync` with Filtering

```csharp
private async Task BroadcastUpdatesAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> source,
    (IChatMessengerClient client, Channel<(AgentKey, AgentResponseUpdate, AiResponse?)> channel)[] clientChannels,
    CancellationToken ct)
{
    try
    {
        await foreach (var update in source.WithCancellation(ct))
        {
            var (agentKey, _, _) = update;
            var promptSource = _chatIdToSource.GetValueOrDefault(agentKey.ChatId);

            foreach (var (client, channel) in clientChannels)
            {
                // WebUI receives everything; others only receive their own source
                if (client.Source == MessageSource.WebUi || client.Source == promptSource)
                {
                    await channel.Writer.WriteAsync(update, ct);
                }
            }
        }
    }
    finally
    {
        foreach (var (_, channel) in clientChannels)
        {
            channel.Writer.TryComplete();
        }
    }
}
```

## Routing Rules Summary

| Prompt Source | WebUI Receives | ServiceBus Receives | Telegram Receives |
|---------------|----------------|---------------------|-------------------|
| WebUI         | Yes            | No                  | No                |
| ServiceBus    | Yes            | Yes                 | No                |
| Telegram      | Yes            | No                  | Yes               |

## Files to Modify

1. **New**: `Domain/DTOs/MessageSource.cs` - enum definition
2. **Modify**: `Domain/Contracts/IChatMessengerClient.cs` - add `Source` property
3. **Modify**: `Domain/DTOs/ChatPrompt.cs` - add `Source` property
4. **Modify**: `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs` - routing logic
5. **Modify**: `Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs` - implement `Source`
6. **Modify**: Telegram client - implement `Source`
7. **Modify**: WebUI client - implement `Source`
8. **Modify**: Tests for all affected components

## Testing Strategy

1. **Unit tests for routing logic**: Verify that responses route correctly based on prompt source
2. **Test edge cases**:
   - Same ChatId receives prompts from different sources over time
   - Unknown ChatId in response stream (should not crash)
3. **Integration tests**: End-to-end verification with real clients
