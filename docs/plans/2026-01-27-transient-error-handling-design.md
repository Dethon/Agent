# Transient Error Handling Design

## Problem

When the PWA goes to background on Android and reconnects later, transient errors (`OperationCanceledException`, empty error messages) appear as permanent chat messages. These pollute the conversation history with messages like "Error: " and "OperationCancelled".

## Solution

Filter transient errors in `StreamingService.cs` instead of displaying them as chat messages. The existing connection status indicator already shows disconnected/reconnecting state, and the existing reconnection flow already handles stream recovery.

## Changes

**File:** `WebChat.Client/Services/Streaming/StreamingService.cs`

### 1. Add transient error detection

```csharp
private static bool IsTransientError(Exception ex) =>
    ex is OperationCanceledException or TaskCanceledException ||
    string.IsNullOrWhiteSpace(ex.Message);
```

### 2. Modify error handling in StreamResponseAsync (lines 203-206)

Before:
```csharp
catch (Exception ex)
{
    dispatcher.Dispatch(new AddMessage(topic.TopicId, CreateErrorMessage($"Error: {ex.Message}")));
}
```

After:
```csharp
catch (Exception ex) when (!IsTransientError(ex))
{
    dispatcher.Dispatch(new AddMessage(topic.TopicId, CreateErrorMessage($"Error: {ex.Message}")));
}
catch
{
    // Transient error - reconnection flow handles recovery
}
```

### 3. Modify error handling in ResumeStreamResponseAsync (lines 365-369)

Same pattern as above.

## Behavior After Change

1. User puts PWA in background
2. SignalR connection drops, stream operation throws `OperationCanceledException`
3. Catch block recognizes transient error → no error message added to chat
4. Partial streaming content remains visible
5. Connection status indicator shows "Reconnecting..."
6. On reconnect, `ReconnectionEffect` triggers `StreamResumeService.TryResumeStreamAsync()`
7. Stream resumes from server buffer, appending to existing partial message

## Non-Changes

- Connection status indicator (already exists, no changes needed)
- Reconnection flow (already works correctly)
- Server-side errors still displayed as chat messages (filtered only for transient client-side errors)
