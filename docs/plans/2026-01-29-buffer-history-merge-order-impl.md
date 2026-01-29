# Buffer-History Merge Ordering Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix buffer-history merge ordering so buffer-only messages are interleaved at the correct position rather than appended at the end.

**Architecture:** Replace the per-message dispatch loop in `ResumeFromBuffer` with a merge algorithm that builds the full interleaved list upfront, using matched message IDs as anchor points, then dispatches a single `MessagesLoaded` action.

**Tech Stack:** C# / .NET 10 / Blazor WASM / Shouldly (tests)

---

### Task 1: Write failing tests for interleaved merge ordering

**Files:**
- Modify: `Tests/Unit/WebChat.Client/State/Pipeline/MessagePipelineTests.cs`

**Step 1: Write the failing tests**

Add these tests to the existing `MessagePipelineTests` class:

```csharp
[Fact]
public void ResumeFromBuffer_InterleavesByAnchorPosition()
{
    // History: [H1(msg-1, user), H2(msg-2, assistant), H3(msg-3, user), H4(msg-4, assistant)]
    var history = new List<ChatHistoryMessage>
    {
        new("msg-1", "user", "Q1", null, null),
        new("msg-2", "assistant", "A1", null, null),
        new("msg-3", "user", "Q2", null, null),
        new("msg-4", "assistant", "A2", null, null)
    };
    _pipeline.LoadHistory("topic-1", history);

    // Buffer: [B1(=msg-2 anchor), B2(new, no ID), B3(=msg-4 anchor)]
    // B2 should appear between msg-2 and msg-3 in final list
    var buffer = new List<ChatStreamMessage>
    {
        new() { MessageId = "msg-2", Content = "A1", IsComplete = true, SequenceNumber = 1 },
        new() { Content = "New message", IsComplete = true, SequenceNumber = 2 },
        new() { MessageId = "msg-4", Content = "A2", IsComplete = true, SequenceNumber = 3 }
    };

    _pipeline.ResumeFromBuffer("topic-1", buffer, null, null, null);

    var messages = _messagesStore.State.MessagesByTopic["topic-1"];
    messages.Count.ShouldBe(5);
    messages[0].Content.ShouldBe("Q1");
    messages[1].Content.ShouldBe("A1");
    messages[2].Content.ShouldBe("New message");  // Interleaved after anchor msg-2
    messages[3].Content.ShouldBe("Q2");
    messages[4].Content.ShouldBe("A2");
}

[Fact]
public void ResumeFromBuffer_LeadingNewMessagesBeforeFirstAnchor()
{
    // History: [H1(msg-1, user), H2(msg-2, assistant)]
    var history = new List<ChatHistoryMessage>
    {
        new("msg-1", "user", "Q1", null, null),
        new("msg-2", "assistant", "A1", null, null)
    };
    _pipeline.LoadHistory("topic-1", history);

    // Buffer: [B0(new), B1(=msg-2 anchor)]
    // B0 should appear before msg-2 (right before the first anchor)
    var buffer = new List<ChatStreamMessage>
    {
        new() { Content = "Leading new", IsComplete = true, SequenceNumber = 1 },
        new() { MessageId = "msg-2", Content = "A1", IsComplete = true, SequenceNumber = 2 }
    };

    _pipeline.ResumeFromBuffer("topic-1", buffer, null, null, null);

    var messages = _messagesStore.State.MessagesByTopic["topic-1"];
    messages.Count.ShouldBe(3);
    messages[0].Content.ShouldBe("Q1");
    messages[1].Content.ShouldBe("Leading new");  // Before anchor msg-2
    messages[2].Content.ShouldBe("A1");
}

[Fact]
public void ResumeFromBuffer_TrailingNewMessagesAfterLastAnchor()
{
    // History: [H1(msg-1, user), H2(msg-2, assistant)]
    var history = new List<ChatHistoryMessage>
    {
        new("msg-1", "user", "Q1", null, null),
        new("msg-2", "assistant", "A1", null, null)
    };
    _pipeline.LoadHistory("topic-1", history);

    // Buffer: [B1(=msg-2 anchor), B2(new trailing)]
    var buffer = new List<ChatStreamMessage>
    {
        new() { MessageId = "msg-2", Content = "A1", IsComplete = true, SequenceNumber = 1 },
        new() { Content = "Trailing new", IsComplete = true, SequenceNumber = 2 }
    };

    _pipeline.ResumeFromBuffer("topic-1", buffer, null, null, null);

    var messages = _messagesStore.State.MessagesByTopic["topic-1"];
    messages.Count.ShouldBe(3);
    messages[0].Content.ShouldBe("Q1");
    messages[1].Content.ShouldBe("A1");
    messages[2].Content.ShouldBe("Trailing new");  // After last anchor
}

[Fact]
public void ResumeFromBuffer_NoAnchors_AppendsAllAtEnd()
{
    // History: [H1(msg-1, user), H2(msg-2, assistant)]
    var history = new List<ChatHistoryMessage>
    {
        new("msg-1", "user", "Q1", null, null),
        new("msg-2", "assistant", "A1", null, null)
    };
    _pipeline.LoadHistory("topic-1", history);

    // Buffer: all new messages, no anchors
    var buffer = new List<ChatStreamMessage>
    {
        new() { Content = "New1", IsComplete = true, SequenceNumber = 1 },
        new() { Content = "New2", IsComplete = true, SequenceNumber = 2 }
    };

    _pipeline.ResumeFromBuffer("topic-1", buffer, null, null, null);

    var messages = _messagesStore.State.MessagesByTopic["topic-1"];
    messages.Count.ShouldBe(4);
    messages[2].Content.ShouldBe("New1");
    messages[3].Content.ShouldBe("New2");
}

[Fact]
public void ResumeFromBuffer_MergesReasoningIntoAnchor()
{
    var history = new List<ChatHistoryMessage>
    {
        new("msg-1", "assistant", "A1", null, null)
    };
    _pipeline.LoadHistory("topic-1", history);

    // Buffer has same content but adds reasoning
    var buffer = new List<ChatStreamMessage>
    {
        new() { MessageId = "msg-1", Content = "A1", Reasoning = "Thought process", IsComplete = true, SequenceNumber = 1 }
    };

    _pipeline.ResumeFromBuffer("topic-1", buffer, null, null, null);

    var messages = _messagesStore.State.MessagesByTopic["topic-1"];
    messages.Count.ShouldBe(1);
    messages[0].Content.ShouldBe("A1");
    messages[0].Reasoning.ShouldBe("Thought process");
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MessagePipelineTests.ResumeFromBuffer_" -v minimal`
Expected: Tests fail (current implementation appends at end instead of interleaving)

**Step 3: Commit failing tests**

```bash
git add Tests/Unit/WebChat.Client/State/Pipeline/MessagePipelineTests.cs
git commit -m "test: add failing tests for buffer-history merge ordering"
```

---

### Task 2: Implement the interleaved merge in ResumeFromBuffer

**Files:**
- Modify: `WebChat.Client/State/Pipeline/MessagePipeline.cs:170-270`

**Step 1: Replace the prompt + turns + TryMergeIntoHistory section**

Replace lines 200-249 of `ResumeFromBuffer` (from the `// Add current prompt` comment through the streaming chunk dispatch) with the new merge algorithm. Keep the streaming chunk dispatch unchanged. Remove the `TryMergeIntoHistory` private method since it moves inline.

The new `ResumeFromBuffer` method body (lines 170-249):

```csharp
public void ResumeFromBuffer(string topicId, IReadOnlyList<ChatStreamMessage> buffer,
    string? currentMessageId, string? currentPrompt, string? currentSenderId)
{
    var existingMessages = messagesStore.State.MessagesByTopic
        .GetValueOrDefault(topicId) ?? [];

    // Build maps for merging: history content for dedup, and history messages by ID for merging
    var historyContent = existingMessages
        .Where(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Content))
        .Select(m => m.Content)
        .ToHashSet();

    var historyById = existingMessages
        .Where(m => !string.IsNullOrEmpty(m.MessageId))
        .ToDictionary(m => m.MessageId!, m => m);

    var (completedTurns, streamingMessage) =
        BufferRebuildUtility.RebuildFromBuffer(buffer, historyContent);

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

    // Filter completed turns to those with content, excluding the current prompt
    var turns = completedTurns
        .Where(t => t.HasContent && !(t.Role == "user" && t.Content == currentPrompt))
        .ToList();

    // Build prompt message if not already in history
    ChatMessageModel? promptMessage = null;
    if (!string.IsNullOrEmpty(currentPrompt) &&
        !existingMessages.Any(m => m.Role == "user" && m.Content == currentPrompt))
    {
        promptMessage = new ChatMessageModel
        {
            Role = "user",
            Content = currentPrompt,
            SenderId = currentSenderId
        };
    }

    // Build merged list
    var merged = MergeBufferWithHistory(existingMessages, turns, historyById, promptMessage);

    // Dispatch merged list as single replacement
    dispatcher.Dispatch(new MessagesLoaded(topicId, merged));

    // Streaming content unchanged - always the latest in-progress content
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

**Step 2: Add the `MergeBufferWithHistory` private method**

Add this method after `ResumeFromBuffer`, replacing the old `TryMergeIntoHistory` method:

```csharp
private static List<ChatMessageModel> MergeBufferWithHistory(
    IReadOnlyList<ChatMessageModel> history,
    List<ChatMessageModel> bufferTurns,
    Dictionary<string, ChatMessageModel> historyById,
    ChatMessageModel? promptMessage)
{
    // Classify buffer turns as anchors (matched by ID in history) or new
    var anchorIds = new HashSet<string>();
    var anchorTurns = new Dictionary<string, ChatMessageModel>();
    var precedingNew = new Dictionary<string, List<ChatMessageModel>>();
    var pendingNew = new List<ChatMessageModel>();
    string? firstAnchorId = null;

    foreach (var turn in bufferTurns)
    {
        if (!string.IsNullOrEmpty(turn.MessageId) && historyById.ContainsKey(turn.MessageId))
        {
            // Anchor: buffer turn matches a history message
            anchorIds.Add(turn.MessageId);
            anchorTurns[turn.MessageId] = turn;
            precedingNew[turn.MessageId] = pendingNew;
            pendingNew = [];
            firstAnchorId ??= turn.MessageId;
        }
        else
        {
            // New: buffer turn not in history
            pendingNew.Add(turn);
        }
    }

    var trailingNew = pendingNew;

    // Build merged list by walking history and inserting new messages at anchor positions
    var merged = new List<ChatMessageModel>(history.Count + bufferTurns.Count);

    foreach (var msg in history)
    {
        // Insert leading new messages (and prompt) before the first anchor
        if (msg.MessageId == firstAnchorId && precedingNew.TryGetValue(firstAnchorId, out var leading) && leading.Count > 0)
        {
            if (promptMessage is not null)
            {
                merged.Add(promptMessage);
                promptMessage = null;
            }
            merged.AddRange(leading);
        }

        // Emit history message, enriching anchors with buffer reasoning/toolcalls
        if (msg.MessageId is not null && anchorTurns.TryGetValue(msg.MessageId, out var bufferTurn))
        {
            var needsReasoning = string.IsNullOrEmpty(msg.Reasoning) && !string.IsNullOrEmpty(bufferTurn.Reasoning);
            var needsToolCalls = string.IsNullOrEmpty(msg.ToolCalls) && !string.IsNullOrEmpty(bufferTurn.ToolCalls);

            merged.Add((needsReasoning || needsToolCalls)
                ? msg with
                {
                    Reasoning = needsReasoning ? bufferTurn.Reasoning : msg.Reasoning,
                    ToolCalls = needsToolCalls ? bufferTurn.ToolCalls : msg.ToolCalls
                }
                : msg);
        }
        else
        {
            merged.Add(msg);
        }

        // Insert new messages that follow this anchor
        if (msg.MessageId is not null && precedingNew.TryGetValue(msg.MessageId, out var following) && msg.MessageId != firstAnchorId)
        {
            merged.AddRange(following);
        }
    }

    // Append prompt if it wasn't placed before an anchor
    if (promptMessage is not null)
    {
        merged.Add(promptMessage);
    }

    // Append trailing new messages
    merged.AddRange(trailingNew);

    return merged;
}
```

**Step 3: Remove the old `TryMergeIntoHistory` method**

Delete lines 251-270 (the `TryMergeIntoHistory` private method). Its logic is now inline in `MergeBufferWithHistory`.

**Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MessagePipelineTests" -v minimal`
Expected: All tests pass including both old and new tests.

**Step 5: Commit**

```bash
git add WebChat.Client/State/Pipeline/MessagePipeline.cs
git commit -m "fix(webchat): interleave buffer messages with history by anchor position

Buffer-only messages now maintain their original order relative to
anchor messages instead of being appended at the end."
```

---

### Task 3: Verify build and run full test suite

**Step 1: Build the solution**

Run: `dotnet build`
Expected: Build succeeds with no errors.

**Step 2: Run full test suite**

Run: `dotnet test Tests/ -v minimal`
Expected: All tests pass, no regressions.

**Step 3: Commit if any fixups were needed**

Only commit if adjustments were required.
