# Message Merging: Multi-MessageId Bubble Consolidation

## Problem

During a single assistant response, the LLM backend may produce multiple distinct MessageIds. Earlier MessageIds often carry only reasoning or tool calls, while only the final MessageId carries the actual content. Without merging, each MessageId renders as a separate chat bubble — producing empty bubbles above the real response.

## Architecture: Unified View-Layer Merge

All message merging is handled in the view layer. StreamingService is a pure data pipeline — it finalizes every message turn to the store unconditionally, with no special-casing for content-less turns.

### StreamingService (data pipeline)

`StreamingService.cs` finalizes every turn that has any content (reasoning, tool calls, or text) via `AddMessage`. No carry-forward, no separator trimming, no content-vs-no-content branching. Both `StreamResponseAsync` and `ResumeStreamResponseAsync` use the same simple pattern:

```csharp
if (isNewMessageTurn && streamingMessage.HasContent)
{
    dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage, currentMessageId));
    streamingMessage = new ChatMessageModel { Role = "assistant" };
}
```

### MessageList.razor (finalized message merge)

`MergeConsecutiveAssistantMessages` runs on every render, collapsing any consecutive run of assistant messages into a single bubble.

**Algorithm**:
1. Walk the message list forward.
2. Collect consecutive assistant messages into a run.
3. Accumulate reasoning (`"\n-----\n"` separator) and tool calls (`"\n"` separator).
4. Take Content from whichever message in the run has it.
5. Use the message with Content as the anchor (preserves its MessageId/Timestamp); fall back to the last message if none has Content.
6. Emit one merged bubble at the end of each run.

**Trailing content-less absorption during streaming**: When streaming is active, if the last merged message is an assistant message with no content but with reasoning or tool calls, `UpdateMessages` absorbs it — removing it from the rendered list and passing its reasoning/toolcalls to `StreamingMessageDisplay` as carried context. When streaming stops, `UpdateStreamingStatus` re-runs `UpdateMessages` so the absorbed message reappears in the finalized list.

### StreamingMessageDisplay.razor (streaming bubble merge)

Accepts `CarriedReasoning` and `CarriedToolCalls` parameters from `MessageList`. Prepends carried context to live streaming content using `MergeField`:

- Reasoning: carried + `"\n-----\n"` + live
- Tool calls: carried + `"\n"` + live

This ensures the streaming bubble shows the full reasoning/toolcalls chain without an empty finalized bubble above it.

## Design Rules

- **Forward accumulation only**: reasoning and tool calls from earlier messages merge into later ones, never backwards.
- **Reasoning separator**: `"\n-----\n"` between blocks from different message turns.
- **Tool calls separator**: `"\n"` between blocks from different message turns.
- **Single merge point**: all merging lives in the view layer (MessageList + StreamingMessageDisplay). No duplication in the data pipeline.

## Scenario Coverage

| Scenario | How it's handled |
|---|---|
| Live streaming, turn change with reasoning-only | Finalized to store → absorbed by MessageList → carried to StreamingMessageDisplay |
| Live streaming, turn change with tool-calls-only | Same as above |
| Buffer resume, consecutive assistant messages | MergeConsecutiveAssistantMessages collapses them |
| Post-stream finalized messages in store | MergeConsecutiveAssistantMessages collapses them |
