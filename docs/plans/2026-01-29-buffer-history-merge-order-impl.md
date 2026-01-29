# Buffer-History Merge Ordering Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix buffer-history merge ordering so buffer-only messages are interleaved at the correct position rather than appended at the end.

**Architecture:** Fix the accumulation bug in `RebuildFromBuffer`, then stop stripping completed turn content (pass empty `historyContent`). With anchors preserved in `completedTurns`, classification and merge happen inline in `ResumeFromBuffer` — no separate merge method needed.

**Tech Stack:** C# / .NET 10 / Blazor WASM / Shouldly (tests)

---

### Task 1: Write failing tests for interleaved merge ordering

Already committed at `55260ba`. Tests are in `Tests/Unit/WebChat.Client/State/Pipeline/MessagePipelineTests.cs`. All 5 tests fail as expected.

---

### Task 2: Fix BufferRebuildUtility accumulation order

**Files:**
- Modify: `WebChat.Client/Services/Streaming/BufferRebuildUtility.cs`

**Context:** There's a bug where single-chunk complete messages (Content + IsComplete in the same message) lose their content. The `continue` in the IsComplete check skips `AccumulateChunk`. This is independent of the merge ordering fix but required for the tests to work.

**Step 1: Move AccumulateChunk before the IsComplete check**

In `RebuildFromBuffer`, find this section (around line 70-96):

```csharp
currentMessageId = msg.MessageId;

// Skip complete markers and errors for accumulation
if (msg.IsComplete || msg.Error is not null)
{
    if (msg.IsComplete && currentAssistantMessage.HasContent)
    {
        var strippedMessage = StripKnownContent(currentAssistantMessage, historyContent);
        if (strippedMessage.HasContent)
        {
            completedTurns.Add(strippedMessage with { MessageId = currentMessageId });
        }

        currentAssistantMessage = new ChatMessageModel { Role = "assistant" };
        needsReasoningSeparator = false;
    }

    continue;
}

currentAssistantMessage = AccumulateChunk(currentAssistantMessage, msg, ref needsReasoningSeparator);
```

Replace with:

```csharp
currentMessageId = msg.MessageId;

// Accumulate content before checking completion (a single chunk can carry both content and IsComplete)
if (!string.IsNullOrEmpty(msg.Content) || !string.IsNullOrEmpty(msg.Reasoning) || !string.IsNullOrEmpty(msg.ToolCalls))
{
    currentAssistantMessage = AccumulateChunk(currentAssistantMessage, msg, ref needsReasoningSeparator);
}

// Handle complete markers and errors
if (msg.IsComplete || msg.Error is not null)
{
    if (msg.IsComplete && currentAssistantMessage.HasContent)
    {
        var strippedMessage = StripKnownContent(currentAssistantMessage, historyContent);
        if (strippedMessage.HasContent)
        {
            completedTurns.Add(strippedMessage with { MessageId = currentMessageId });
        }

        currentAssistantMessage = new ChatMessageModel { Role = "assistant" };
        needsReasoningSeparator = false;
    }

    continue;
}
```

**Step 2: Run existing BufferRebuildUtility tests**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~BufferRebuildUtilityTests" -v minimal`
Expected: All existing tests still pass (no regressions).

**Step 3: Commit**

```bash
git add WebChat.Client/Services/Streaming/BufferRebuildUtility.cs
git commit -m "fix(webchat): accumulate content before checking IsComplete in buffer rebuild

Single-chunk complete messages (Content + IsComplete in same message)
previously lost their content because the continue skipped AccumulateChunk."
```

---

### Task 3: Simplify ResumeFromBuffer with inline merge

**Files:**
- Modify: `WebChat.Client/State/Pipeline/MessagePipeline.cs`

**Key insight:** Don't strip completed turn content — pass empty `historyContent` to `RebuildFromBuffer`. This preserves anchor MessageIds in `completedTurns`, so classification and merge happen in one place without a separate method.

**Step 1: Replace ResumeFromBuffer body**

Replace the entire `ResumeFromBuffer` method (lines 170-253) with:

```csharp
public void ResumeFromBuffer(string topicId, IReadOnlyList<ChatStreamMessage> buffer,
    string? currentMessageId, string? currentPrompt, string? currentSenderId)
{
    var existingMessages = messagesStore.State.MessagesByTopic
        .GetValueOrDefault(topicId) ?? [];

    var historyById = existingMessages
        .Where(m => !string.IsNullOrEmpty(m.MessageId))
        .ToDictionary(m => m.MessageId!, m => m);

    // Don't strip completed turn content — we need anchor MessageIds for positioning.
    // Only strip the streaming message (below).
    var (completedTurns, rawStreamingMessage) =
        BufferRebuildUtility.RebuildFromBuffer(buffer, []);

    // Strip streaming message against history content
    var historyContent = existingMessages
        .Where(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Content))
        .Select(m => m.Content)
        .ToHashSet();
    var streamingMessage = BufferRebuildUtility.StripKnownContent(rawStreamingMessage, historyContent);

    lock (_lock)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "Pipeline.ResumeFromBuffer: topic={TopicId}, bufferCount={BufferCount}, " +
                "completedTurns={CompletedTurns}, hasStreamingContent={HasStreaming}",
                topicId, buffer.Count, completedTurns.Count, streamingMessage.HasContent);
        }
    }

    // Classify completed turns: anchors (MessageId in history) track position,
    // new messages are grouped by which anchor they follow
    string? lastAnchorId = null;
    var followingNew = new Dictionary<string, List<ChatMessageModel>>();
    var leadingNew = new List<ChatMessageModel>();

    foreach (var turn in completedTurns.Where(t =>
                 t.HasContent && !(t.Role == "user" && t.Content == currentPrompt)))
    {
        if (!string.IsNullOrEmpty(turn.MessageId) && historyById.ContainsKey(turn.MessageId))
        {
            followingNew[turn.MessageId] = [];
            lastAnchorId = turn.MessageId;
        }
        else if (lastAnchorId is not null)
        {
            followingNew[lastAnchorId].Add(turn);
        }
        else
        {
            leadingNew.Add(turn);
        }
    }

    // Build merged list: walk history, enrich anchors, insert new messages at anchor positions
    var merged = new List<ChatMessageModel>(existingMessages.Count + completedTurns.Count);
    var leadingInserted = false;

    foreach (var msg in existingMessages)
    {
        // Insert leading new messages before the first anchor
        if (!leadingInserted && msg.MessageId is not null && followingNew.ContainsKey(msg.MessageId))
        {
            merged.AddRange(leadingNew);
            leadingInserted = true;
        }

        // Enrich anchor with buffer reasoning/toolcalls, or pass through unchanged
        var anchorTurn = (msg.MessageId is not null &&
            completedTurns.FirstOrDefault(t => t.MessageId == msg.MessageId) is { } match)
            ? match : null;

        if (anchorTurn is not null)
        {
            var nr = string.IsNullOrEmpty(msg.Reasoning) && !string.IsNullOrEmpty(anchorTurn.Reasoning);
            var nt = string.IsNullOrEmpty(msg.ToolCalls) && !string.IsNullOrEmpty(anchorTurn.ToolCalls);
            merged.Add((nr || nt)
                ? msg with
                {
                    Reasoning = nr ? anchorTurn.Reasoning : msg.Reasoning,
                    ToolCalls = nt ? anchorTurn.ToolCalls : msg.ToolCalls
                }
                : msg);
        }
        else
        {
            merged.Add(msg);
        }

        // Insert new messages that follow this anchor
        if (msg.MessageId is not null && followingNew.TryGetValue(msg.MessageId, out var following))
        {
            merged.AddRange(following);
        }
    }

    // Append leading new if no anchors were found
    if (!leadingInserted)
    {
        merged.AddRange(leadingNew);
    }

    // Add current prompt if not already present
    if (!string.IsNullOrEmpty(currentPrompt) &&
        !existingMessages.Any(m => m.Role == "user" && m.Content == currentPrompt))
    {
        merged.Add(new ChatMessageModel
        {
            Role = "user",
            Content = currentPrompt,
            SenderId = currentSenderId
        });
    }

    dispatcher.Dispatch(new MessagesLoaded(topicId, merged));

    // Streaming content — enrich history or dispatch chunk
    if (streamingMessage.HasContent)
    {
        if (!string.IsNullOrEmpty(currentMessageId) &&
            historyById.TryGetValue(currentMessageId, out var historyMsg))
        {
            var needsReasoning = string.IsNullOrEmpty(historyMsg.Reasoning) && !string.IsNullOrEmpty(streamingMessage.Reasoning);
            var needsToolCalls = string.IsNullOrEmpty(historyMsg.ToolCalls) && !string.IsNullOrEmpty(streamingMessage.ToolCalls);

            if (needsReasoning || needsToolCalls)
            {
                var enriched = historyMsg with
                {
                    Reasoning = needsReasoning ? streamingMessage.Reasoning : historyMsg.Reasoning,
                    ToolCalls = needsToolCalls ? streamingMessage.ToolCalls : historyMsg.ToolCalls
                };
                dispatcher.Dispatch(new UpdateMessage(topicId, historyMsg.MessageId!, enriched));
                return;
            }
        }

        dispatcher.Dispatch(new StreamChunk(
            topicId,
            streamingMessage.Content,
            streamingMessage.Reasoning,
            streamingMessage.ToolCalls,
            currentMessageId));
    }
}
```

**Step 2: Remove `TryMergeIntoHistory`**

Delete the `TryMergeIntoHistory` private method. Its enrichment logic is now inline in the merge loop.

**Step 3: Run all MessagePipeline tests**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MessagePipelineTests" -v minimal`
Expected: All tests pass (old and new).

**Step 4: Commit**

```bash
git add WebChat.Client/State/Pipeline/MessagePipeline.cs
git commit -m "fix(webchat): interleave buffer messages with history by anchor position

Stop stripping completed turn content so anchor MessageIds are preserved
in completedTurns. Classification and merge happen inline — no separate
merge method needed. Buffer-only messages now appear at the correct
position relative to anchors instead of being appended at the end."
```

---

### Task 4: Verify build and run full test suite

**Step 1: Build the solution**

Run: `dotnet build`
Expected: Build succeeds with no errors.

**Step 2: Run full test suite**

Run: `dotnet test Tests/ -v minimal`
Expected: All tests pass, no regressions.

**Step 3: Commit if any fixups were needed**

Only commit if adjustments were required.
