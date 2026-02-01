# Assistant Response Timestamps

Add timestamps to assistant response messages so they display in the UI and persist in Redis history.

## Two Paths

### Path 1: Persistence (OpenRouter -> Redis -> History)

Timestamps flow through the framework's existing `AdditionalProperties` mechanism.

1. **`OpenRouterChatClient.GetStreamingResponseAsync`** - stamp each `ChatResponseUpdate` with `AdditionalProperties["Timestamp"] = DateTimeOffset.UtcNow`
2. Framework's `ToChatResponse()` merges into assembled `ChatMessage.AdditionalProperties` (last chunk wins = completion time)
3. `RedisChatMessageStore.InvokedAsync` persists automatically - no changes
4. `ChatHub.GetHistory` reads via `GetTimestamp()` - no changes

### Path 2: WebChat Streaming (live updates -> client finalization)

Timestamps propagate through streaming chunks to the finalized client model.

1. **`ChatStreamMessage`** - add `DateTimeOffset? Timestamp` property
2. **`WebChatMessengerClient.ProcessResponseStreamAsync`** - set `Timestamp = DateTimeOffset.UtcNow` on each `ChatStreamMessage`
3. **`BufferRebuildUtility.AccumulateChunk`** - carry timestamp forward from chunks (last wins)
4. `StreamingService.ProcessStreamAsync` - no changes needed, accumulated model already carries timestamp
5. `ChatMessage.razor` - no changes needed, already renders timestamps when not null

## Files to Change

| File | Change |
|------|--------|
| `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs` | Stamp updates with timestamp |
| `Domain/DTOs/WebChat/ChatStreamMessage.cs` | Add `Timestamp` property |
| `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs` | Set timestamp on stream messages |
| `WebChat.Client/Services/Streaming/BufferRebuildUtility.cs` | Carry timestamp in accumulation |

## What Already Works (no changes needed)

- `ChatMessage.AdditionalProperties` serialization round-trip (tested)
- `GetTimestamp()` / `SetTimestamp()` extension methods
- `ChatHub.GetHistory` timestamp extraction
- `MessagePipeline.LoadHistory` timestamp transfer to `ChatMessageModel`
- `ChatMessage.razor` timestamp rendering and CSS styling
- `RedisChatMessageStore` persistence
