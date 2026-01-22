# WebChat User Message Sync Design

## Problem

1. **User prompts don't sync**: When user A sends messages, user B (watching the same topic in another browser) doesn't see them
2. **Ordering broken**: User prompts render grouped together instead of chronologically interleaved with agent responses

## Root Cause

- User messages are only added to the local client's state (`AddMessage` dispatch)
- The server buffer (`StreamState.BufferedMessages`) only contains assistant content
- `_currentPrompts[topicId]` stores only ONE prompt (overwrites on concurrent sends)
- No SignalR notification broadcasts user messages to other browsers

## Solution

Write user messages to the stream buffer and broadcast via SignalR.

### Data Model Changes

**New `UserMessageInfo` record:**
```csharp
public record UserMessageInfo(string? SenderId);
```

**Add to `ChatStreamMessage`:**
```csharp
public UserMessageInfo? UserMessage { get; init; }  // null = assistant message
```

When `UserMessage` is not null, `Content` contains the user's text.

**New notification:**
```csharp
public record UserMessageNotification(string TopicId, string Content, string? SenderId);
```

### Server-Side Flow

When user sends a message (both `EnqueuePromptAndGetResponses` and `EnqueuePrompt`):

1. Create/reuse stream
2. Increment pending count
3. **Write user message to buffer** (new)
4. **Broadcast `UserMessageNotification`** (new)
5. Notify `StreamChangeType.Started`
6. Write prompt to channel
7. Return stream

User message written to buffer:
```csharp
var userMessage = new ChatStreamMessage
{
    Content = message,
    UserMessage = new UserMessageInfo(sender)
};
await streamManager.WriteMessageAsync(topicId, userMessage, cancellationToken);
```

### Client-Side Handling

**Real-time (browser connected):**
- Server broadcasts `UserMessageNotification`
- `HubEventDispatcher` receives via `OnUserMessage` handler
- Dispatches `AddMessage` action with user role

**Refresh/Reconnect:**
- `StreamResumeService.TryResumeStreamAsync` calls `GetStreamState`
- `BufferedMessages` contains both user AND assistant messages in chronological order
- `BufferRebuildUtility.RebuildFromBuffer` processes mixed messages:
  - User messages (`UserMessage != null`) → add to completed list as user role
  - Assistant content → existing logic (rebuild streaming message)
- Returns `(List<ChatMessageModel> completedMessages, ChatMessageModel streamingMessage)`

### File Changes

**Server-side:**

| File | Change |
|------|--------|
| `Domain/DTOs/WebChat/ChatStreamMessage.cs` | Add `UserMessageInfo? UserMessage` property |
| `Domain/DTOs/WebChat/UserMessageInfo.cs` | New record |
| `Domain/DTOs/WebChat/UserMessageNotification.cs` | New record for SignalR broadcast |
| `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs` | Write user message to buffer + broadcast in both enqueue methods |
| `Domain/Contracts/INotifier.cs` | Add `NotifyUserMessageAsync` method |
| `Infrastructure/Clients/Messaging/SignalRNotifier.cs` | Implement `NotifyUserMessageAsync` |

**Client-side:**

| File | Change |
|------|--------|
| `WebChat.Client/Contracts/IChatConnection.cs` | Add `OnUserMessage` event handler |
| `WebChat.Client/Services/Chat/ChatConnectionService.cs` | Register `OnUserMessage` handler |
| `WebChat.Client/State/Hub/HubEventDispatcher.cs` | Handle notification → dispatch `AddMessage` |
| `WebChat.Client/Services/Utilities/BufferRebuildUtility.cs` | Handle mixed user/assistant messages |

### Ordering

The buffer maintains chronological order via `WriteMessageAsync` which assigns incrementing sequence numbers. User and assistant messages naturally interleave in correct order.
