# Pipeline & BufferRebuildUtility Simplification

## Problem

After implementing the buffer-history merge ordering (anchor-based interleaving), responsibility is split awkwardly across three classes:

- **BufferRebuildUtility** — rebuilds buffer into turns, strips content, accumulates chunks
- **MessagePipeline.ResumeFromBuffer** — calls BufferRebuildUtility, then does anchor classification, merge building, enrichment, streaming dispatch (~150 lines)
- **StreamResumeService** — calls BufferRebuildUtility to extract streaming message, then calls MessagePipeline again (buffer rebuilt twice)

This creates unclear ownership, duplicated work (double rebuild), and a 150-line method in MessagePipeline that mixes pure transformation with dispatching.

## Design

### Single Owner: BufferRebuildUtility

`BufferRebuildUtility` becomes the single owner of the entire buffer-to-merged-messages transformation. It takes the raw buffer **and** existing history, and returns a ready-to-dispatch result.

### New API

```csharp
public static class BufferRebuildUtility
{
    // Main entry: buffer + history → merged list + streaming message
    public static BufferResumeResult ResumeFromBuffer(
        IReadOnlyList<ChatStreamMessage> buffer,
        IReadOnlyList<ChatMessageModel> existingHistory,
        string? currentPrompt,
        string? currentSenderId);

    // Internal helper for chunk accumulation
    internal static ChatMessageModel AccumulateChunk(
        ChatMessageModel streamingMessage,
        ChatStreamMessage chunk,
        ref bool needsReasoningSeparator);
}

public record BufferResumeResult(
    List<ChatMessageModel> MergedMessages,
    ChatMessageModel StreamingMessage);
```

### Removed API

- `RebuildFromBuffer(buffer, historyContent)` — replaced by `ResumeFromBuffer` which takes full history
- `StripKnownContent(message, historyContent)` — becomes private helper
- `StripKnownContentById(message, messageId, historyContentById)` — deleted (unused in production code)

### Algorithm inside ResumeFromBuffer

1. Rebuild buffer into completed turns + raw streaming message (existing loop logic)
2. Build `historyById` dictionary from existing history
3. Build `historyContent` HashSet from existing history (for content stripping)
4. Classify completed turns as anchor (MessageId in history) vs new, group by position relative to anchors
5. Walk history in order, interleave new messages at anchor positions, enrich anchors with buffer reasoning/toolCalls
6. Append current prompt if not already in history
7. Strip streaming message content against history
8. Return `BufferResumeResult(mergedMessages, strippedStreamingMessage)`

### Simplified MessagePipeline.ResumeFromBuffer

Shrinks from ~150 lines to ~25 lines. Takes pre-built result, only dispatches:

```csharp
public void ResumeFromBuffer(BufferResumeResult result,
    string topicId, string? currentMessageId)
{
    dispatcher.Dispatch(new MessagesLoaded(topicId, result.MergedMessages));

    if (!result.StreamingMessage.HasContent) return;

    // Enrich existing history message or dispatch as streaming chunk
    var historyMsg = FindInHistory(topicId, currentMessageId);
    if (historyMsg is not null && NeedsEnrichment(historyMsg, result.StreamingMessage))
    {
        dispatcher.Dispatch(new UpdateMessage(topicId, currentMessageId!,
            EnrichMessage(historyMsg, result.StreamingMessage)));
        return;
    }

    dispatcher.Dispatch(new StreamChunk(topicId,
        result.StreamingMessage.Content,
        result.StreamingMessage.Reasoning,
        result.StreamingMessage.ToolCalls,
        currentMessageId));
}
```

### Simplified StreamResumeService

Single rebuild, result passed to both consumers:

```csharp
var existingHistory = messagesStore.State.MessagesByTopic
    .GetValueOrDefault(topic.TopicId) ?? [];
var result = BufferRebuildUtility.ResumeFromBuffer(
    state.BufferedMessages, existingHistory, state.CurrentPrompt, state.CurrentSenderId);

await streamingService.TryStartResumeStreamAsync(topic, result.StreamingMessage, state.CurrentMessageId);
pipeline.ResumeFromBuffer(result, topic.TopicId, state.CurrentMessageId);
```

## Test Migration

### BufferRebuildUtilityTests

- Update existing `RebuildFromBuffer` tests to use new `ResumeFromBuffer` signature
- Move anchor/interleave tests from `MessagePipelineTests` here (the logic now lives in the utility)
- Delete `StripKnownContentById` tests (4 tests)
- `StripKnownContent` tests become internal/removed if method goes private

### MessagePipelineTests

- `ResumeFromBuffer` tests simplify: verify dispatching given a `BufferResumeResult`, not merge logic
- All other pipeline tests unchanged

### StreamResumeServiceTests

- Update to verify single `ResumeFromBuffer` call and result pass-through

### Unchanged

- `MessagePipelineIntegrationTests`
- All other pipeline methods (`SubmitUserMessage`, `AccumulateChunk`, `FinalizeMessage`, `LoadHistory`, `Reset`)
- Reducers, stores, other effects

## Scope

| File | Change |
|------|--------|
| `WebChat.Client/Services/Streaming/BufferRebuildUtility.cs` | Absorb merge/anchor logic, new API, delete `StripKnownContentById` |
| `WebChat.Client/State/Pipeline/MessagePipeline.cs` | Shrink `ResumeFromBuffer` to dispatch-only |
| `WebChat.Client/State/Pipeline/IMessagePipeline.cs` | Update `ResumeFromBuffer` signature |
| `WebChat.Client/Services/Streaming/StreamResumeService.cs` | Single rebuild, pass result |
| `Tests/Unit/WebChat/Client/BufferRebuildUtilityTests.cs` | New signature, absorb merge tests |
| `Tests/Unit/WebChat.Client/State/Pipeline/MessagePipelineTests.cs` | Simplify resume tests |
| `Tests/Unit/WebChat/Client/StreamResumeServiceTests.cs` | Update for new flow |

## Net Effect

- `BufferRebuildUtility`: ~195 → ~280 lines (absorbs merge logic), pure static — easy to test
- `MessagePipeline.ResumeFromBuffer`: ~150 → ~25 lines
- Buffer rebuilt once instead of twice
- Dead code removed
- Clear ownership: utility transforms, pipeline dispatches
