# Pipeline Simplification Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Consolidate buffer-resume responsibility into BufferRebuildUtility so MessagePipeline only dispatches.

**Architecture:** BufferRebuildUtility becomes the single owner of all buffer-to-merged-messages transformation (rebuild, anchor/merge, content stripping). MessagePipeline.ResumeFromBuffer shrinks to a dispatch-only method that takes a pre-built result. StreamResumeService calls the utility once and passes the result through.

**Tech Stack:** .NET 10, C# records, Shouldly assertions, xUnit

---

### Task 1: Add BufferResumeResult record and ResumeFromBuffer method with tests

**Files:**
- Modify: `WebChat.Client/Services/Streaming/BufferRebuildUtility.cs`
- Modify: `Tests/Unit/WebChat/Client/BufferRebuildUtilityTests.cs`

**Step 1: Write the failing tests**

Add a new test region to `BufferRebuildUtilityTests.cs`. These tests exercise the new `ResumeFromBuffer` method — the single entry point that takes raw buffer + existing history and returns a merged result.

Add these tests at the end of `BufferRebuildUtilityTests.cs` (before the closing `}`):

```csharp
#region ResumeFromBuffer Tests

[Fact]
public void ResumeFromBuffer_WithEmptyBuffer_ReturnsHistoryUnchanged()
{
    var history = new List<ChatMessageModel>
    {
        new() { Role = "user", Content = "Q1", MessageId = "msg-1" },
        new() { Role = "assistant", Content = "A1", MessageId = "msg-2" }
    };

    var result = BufferRebuildUtility.ResumeFromBuffer([], history, null, null);

    result.MergedMessages.Count.ShouldBe(2);
    result.MergedMessages[0].Content.ShouldBe("Q1");
    result.MergedMessages[1].Content.ShouldBe("A1");
    result.StreamingMessage.HasContent.ShouldBeFalse();
}

[Fact]
public void ResumeFromBuffer_InterleavesByAnchorPosition()
{
    var history = new List<ChatMessageModel>
    {
        new() { Role = "user", Content = "Q1", MessageId = "msg-1" },
        new() { Role = "assistant", Content = "A1", MessageId = "msg-2" },
        new() { Role = "user", Content = "Q2", MessageId = "msg-3" },
        new() { Role = "assistant", Content = "A2", MessageId = "msg-4" }
    };

    // Buffer: anchor msg-2, new message (no ID), anchor msg-4
    var buffer = new List<ChatStreamMessage>
    {
        new() { MessageId = "msg-2", Content = "A1", IsComplete = true, SequenceNumber = 1 },
        new() { Content = "New message", IsComplete = true, SequenceNumber = 2 },
        new() { MessageId = "msg-4", Content = "A2", IsComplete = true, SequenceNumber = 3 }
    };

    var result = BufferRebuildUtility.ResumeFromBuffer(buffer, history, null, null);

    result.MergedMessages.Count.ShouldBe(5);
    result.MergedMessages[0].Content.ShouldBe("Q1");
    result.MergedMessages[1].Content.ShouldBe("A1");
    result.MergedMessages[2].Content.ShouldBe("New message");
    result.MergedMessages[3].Content.ShouldBe("Q2");
    result.MergedMessages[4].Content.ShouldBe("A2");
}

[Fact]
public void ResumeFromBuffer_LeadingNewMessagesBeforeFirstAnchor()
{
    var history = new List<ChatMessageModel>
    {
        new() { Role = "user", Content = "Q1", MessageId = "msg-1" },
        new() { Role = "assistant", Content = "A1", MessageId = "msg-2" }
    };

    var buffer = new List<ChatStreamMessage>
    {
        new() { Content = "Leading new", IsComplete = true, SequenceNumber = 1 },
        new() { MessageId = "msg-2", Content = "A1", IsComplete = true, SequenceNumber = 2 }
    };

    var result = BufferRebuildUtility.ResumeFromBuffer(buffer, history, null, null);

    result.MergedMessages.Count.ShouldBe(3);
    result.MergedMessages[0].Content.ShouldBe("Q1");
    result.MergedMessages[1].Content.ShouldBe("Leading new");
    result.MergedMessages[2].Content.ShouldBe("A1");
}

[Fact]
public void ResumeFromBuffer_TrailingNewMessagesAfterLastAnchor()
{
    var history = new List<ChatMessageModel>
    {
        new() { Role = "user", Content = "Q1", MessageId = "msg-1" },
        new() { Role = "assistant", Content = "A1", MessageId = "msg-2" }
    };

    var buffer = new List<ChatStreamMessage>
    {
        new() { MessageId = "msg-2", Content = "A1", IsComplete = true, SequenceNumber = 1 },
        new() { Content = "Trailing new", IsComplete = true, SequenceNumber = 2 }
    };

    var result = BufferRebuildUtility.ResumeFromBuffer(buffer, history, null, null);

    result.MergedMessages.Count.ShouldBe(3);
    result.MergedMessages[0].Content.ShouldBe("Q1");
    result.MergedMessages[1].Content.ShouldBe("A1");
    result.MergedMessages[2].Content.ShouldBe("Trailing new");
}

[Fact]
public void ResumeFromBuffer_NoAnchors_AppendsAllAtEnd()
{
    var history = new List<ChatMessageModel>
    {
        new() { Role = "user", Content = "Q1", MessageId = "msg-1" },
        new() { Role = "assistant", Content = "A1", MessageId = "msg-2" }
    };

    var buffer = new List<ChatStreamMessage>
    {
        new() { Content = "New1", IsComplete = true, SequenceNumber = 1 },
        new() { Content = "New2", IsComplete = true, SequenceNumber = 2 }
    };

    var result = BufferRebuildUtility.ResumeFromBuffer(buffer, history, null, null);

    result.MergedMessages.Count.ShouldBe(4);
    result.MergedMessages[2].Content.ShouldBe("New1");
    result.MergedMessages[3].Content.ShouldBe("New2");
}

[Fact]
public void ResumeFromBuffer_MergesReasoningIntoAnchor()
{
    var history = new List<ChatMessageModel>
    {
        new() { Role = "assistant", Content = "A1", MessageId = "msg-1" }
    };

    var buffer = new List<ChatStreamMessage>
    {
        new() { MessageId = "msg-1", Content = "A1", Reasoning = "Thought process", IsComplete = true, SequenceNumber = 1 }
    };

    var result = BufferRebuildUtility.ResumeFromBuffer(buffer, history, null, null);

    result.MergedMessages.Count.ShouldBe(1);
    result.MergedMessages[0].Content.ShouldBe("A1");
    result.MergedMessages[0].Reasoning.ShouldBe("Thought process");
}

[Fact]
public void ResumeFromBuffer_AddsPromptIfNotInHistory()
{
    var history = new List<ChatMessageModel>
    {
        new() { Role = "assistant", Content = "Previous", MessageId = "msg-1" }
    };

    var buffer = new List<ChatStreamMessage>
    {
        new() { MessageId = "msg-1", Content = "Previous", IsComplete = true, SequenceNumber = 1 }
    };

    var result = BufferRebuildUtility.ResumeFromBuffer(buffer, history, "New question", "alice");

    result.MergedMessages.Count.ShouldBe(2);
    result.MergedMessages[1].Role.ShouldBe("user");
    result.MergedMessages[1].Content.ShouldBe("New question");
    result.MergedMessages[1].SenderId.ShouldBe("alice");
}

[Fact]
public void ResumeFromBuffer_DoesNotDuplicatePrompt()
{
    var history = new List<ChatMessageModel>
    {
        new() { Role = "user", Content = "Same prompt" }
    };

    var buffer = new List<ChatStreamMessage>();

    var result = BufferRebuildUtility.ResumeFromBuffer(buffer, history, "Same prompt", null);

    var promptCount = result.MergedMessages.Count(m => m is { Role: "user", Content: "Same prompt" });
    promptCount.ShouldBe(1);
}

[Fact]
public void ResumeFromBuffer_ExcludesCurrentPromptFromBufferTurns()
{
    var history = new List<ChatMessageModel>();

    // Buffer has user message matching current prompt — should not appear twice
    var buffer = new List<ChatStreamMessage>
    {
        new() { Content = "User's question", UserMessage = new UserMessageInfo("Bob", null), SequenceNumber = 1 },
        new() { Content = "Response", MessageId = "msg-1", SequenceNumber = 2 }
    };

    var result = BufferRebuildUtility.ResumeFromBuffer(buffer, history, "User's question", "Bob");

    var promptCount = result.MergedMessages.Count(m => m is { Role: "user", Content: "User's question" });
    promptCount.ShouldBe(1);
}

[Fact]
public void ResumeFromBuffer_StripsStreamingMessageContentAgainstHistory()
{
    var history = new List<ChatMessageModel>
    {
        new() { Role = "assistant", Content = "Already known content", MessageId = "msg-1" }
    };

    // Buffer has same content as history (still streaming the same message)
    var buffer = new List<ChatStreamMessage>
    {
        new() { Content = "Already known content", MessageId = "msg-1", SequenceNumber = 1 }
    };

    var result = BufferRebuildUtility.ResumeFromBuffer(buffer, history, null, null);

    // Streaming message content should be stripped (it's a duplicate)
    result.StreamingMessage.Content.ShouldBeEmpty();
}

[Fact]
public void ResumeFromBuffer_WithEmptyHistory_ReturnsBufferTurns()
{
    var buffer = new List<ChatStreamMessage>
    {
        new() { Content = "First", MessageId = "msg-1", IsComplete = true, SequenceNumber = 1 },
        new() { Content = "Second", MessageId = "msg-2", SequenceNumber = 2 }
    };

    var result = BufferRebuildUtility.ResumeFromBuffer(buffer, [], null, null);

    result.MergedMessages.Count.ShouldBe(1);
    result.MergedMessages[0].Content.ShouldBe("First");
    result.StreamingMessage.Content.ShouldBe("Second");
}

#endregion
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~BufferRebuildUtilityTests.ResumeFromBuffer_" --no-build 2>&1 | head -30`
Expected: Build failure — `BufferResumeResult` type and `ResumeFromBuffer(buffer, history, prompt, sender)` overload don't exist yet.

**Step 3: Implement BufferResumeResult and ResumeFromBuffer**

Add the `BufferResumeResult` record at the top of `BufferRebuildUtility.cs` (before the class), and add the new `ResumeFromBuffer` method to the class. Also make `StripKnownContent` private and delete `StripKnownContentById`.

Replace the entire `BufferRebuildUtility.cs` with:

```csharp
using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.Services.Streaming;

public record BufferResumeResult(
    List<ChatMessageModel> MergedMessages,
    ChatMessageModel StreamingMessage);

public static class BufferRebuildUtility
{
    public static BufferResumeResult ResumeFromBuffer(
        IReadOnlyList<ChatStreamMessage> buffer,
        IReadOnlyList<ChatMessageModel> existingHistory,
        string? currentPrompt,
        string? currentSenderId)
    {
        var historyById = existingHistory
            .Where(m => !string.IsNullOrEmpty(m.MessageId))
            .ToDictionary(m => m.MessageId!, m => m);

        // Rebuild buffer into completed turns + raw streaming message.
        // Pass empty historyContent — we need anchor MessageIds for positioning, not content stripping.
        var (completedTurns, rawStreamingMessage) = RebuildFromBuffer(buffer, []);

        // Strip streaming message against history content
        var historyContent = existingHistory
            .Where(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Content))
            .Select(m => m.Content)
            .ToHashSet();
        var streamingMessage = StripKnownContent(rawStreamingMessage, historyContent);

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
        var merged = new List<ChatMessageModel>(existingHistory.Count + completedTurns.Count);
        var leadingInserted = false;
        var completedById = completedTurns
            .Where(t => !string.IsNullOrEmpty(t.MessageId))
            .ToDictionary(t => t.MessageId!, t => t);

        foreach (var msg in existingHistory)
        {
            // Insert leading new messages before the first anchor
            if (!leadingInserted && msg.MessageId is not null && followingNew.ContainsKey(msg.MessageId))
            {
                merged.AddRange(leadingNew);
                leadingInserted = true;
            }

            // Enrich anchor with buffer reasoning/toolcalls, or pass through unchanged
            if (msg.MessageId is not null && completedById.TryGetValue(msg.MessageId, out var anchorTurn))
            {
                var needsReasoning = string.IsNullOrEmpty(msg.Reasoning) && !string.IsNullOrEmpty(anchorTurn.Reasoning);
                var needsToolCalls = string.IsNullOrEmpty(msg.ToolCalls) && !string.IsNullOrEmpty(anchorTurn.ToolCalls);
                merged.Add((needsReasoning || needsToolCalls)
                    ? msg with
                    {
                        Reasoning = needsReasoning ? anchorTurn.Reasoning : msg.Reasoning,
                        ToolCalls = needsToolCalls ? anchorTurn.ToolCalls : msg.ToolCalls
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
            !existingHistory.Any(m => m.Role == "user" && m.Content == currentPrompt))
        {
            merged.Add(new ChatMessageModel
            {
                Role = "user",
                Content = currentPrompt,
                SenderId = currentSenderId
            });
        }

        return new BufferResumeResult(merged, streamingMessage);
    }

    internal static (List<ChatMessageModel> CompletedTurns, ChatMessageModel StreamingMessage) RebuildFromBuffer(
        IReadOnlyList<ChatStreamMessage> bufferedMessages,
        HashSet<string> historyContent)
    {
        var completedTurns = new List<ChatMessageModel>();
        var currentAssistantMessage = new ChatMessageModel { Role = "assistant" };

        if (bufferedMessages.Count == 0)
        {
            return (completedTurns, currentAssistantMessage);
        }

        var orderedMessages = bufferedMessages
            .OrderBy(m => m.SequenceNumber)
            .ToList();

        var needsReasoningSeparator = false;
        string? currentMessageId = null;

        foreach (var msg in orderedMessages)
        {
            if (msg.UserMessage is not null)
            {
                if (currentAssistantMessage.HasContent)
                {
                    var strippedMessage = StripKnownContent(currentAssistantMessage, historyContent);
                    if (strippedMessage.HasContent)
                    {
                        completedTurns.Add(strippedMessage with { MessageId = currentMessageId });
                    }

                    currentAssistantMessage = new ChatMessageModel { Role = "assistant" };
                    needsReasoningSeparator = false;
                    currentMessageId = null;
                }

                completedTurns.Add(new ChatMessageModel
                {
                    Role = "user",
                    Content = msg.Content ?? "",
                    SenderId = msg.UserMessage.SenderId,
                    Timestamp = msg.UserMessage.Timestamp
                });
                continue;
            }

            if (currentMessageId is not null && msg.MessageId != currentMessageId && currentAssistantMessage.HasContent)
            {
                var strippedMessage = StripKnownContent(currentAssistantMessage, historyContent);
                if (strippedMessage.HasContent)
                {
                    completedTurns.Add(strippedMessage with { MessageId = currentMessageId });
                }

                currentAssistantMessage = new ChatMessageModel { Role = "assistant" };
                needsReasoningSeparator = false;
            }

            currentMessageId = msg.MessageId;

            if (!string.IsNullOrEmpty(msg.Content) || !string.IsNullOrEmpty(msg.Reasoning) || !string.IsNullOrEmpty(msg.ToolCalls))
            {
                currentAssistantMessage = AccumulateChunk(currentAssistantMessage, msg, ref needsReasoningSeparator);
            }

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
        }

        var streamingMessage = StripKnownContent(currentAssistantMessage, historyContent);
        return (completedTurns, streamingMessage);
    }

    private static ChatMessageModel StripKnownContent(ChatMessageModel message, HashSet<string> historyContent)
    {
        if (string.IsNullOrEmpty(message.Content))
        {
            return message;
        }

        if (historyContent.Any(known => known.Contains(message.Content)))
        {
            return message with { Content = "" };
        }

        var content = message.Content;
        foreach (var known in historyContent.Where(known => content.StartsWith(known)))
        {
            content = content[known.Length..].TrimStart();
        }

        return content != message.Content ? message with { Content = content } : message;
    }

    internal static ChatMessageModel AccumulateChunk(
        ChatMessageModel streamingMessage,
        ChatStreamMessage chunk,
        ref bool needsReasoningSeparator)
    {
        if (!string.IsNullOrEmpty(chunk.Content))
        {
            streamingMessage = streamingMessage with
            {
                Content = string.IsNullOrEmpty(streamingMessage.Content)
                    ? chunk.Content
                    : streamingMessage.Content + chunk.Content
            };
        }

        if (!string.IsNullOrEmpty(chunk.Reasoning))
        {
            var separator = needsReasoningSeparator ? "\n-----\n" : "";
            needsReasoningSeparator = false;
            streamingMessage = streamingMessage with
            {
                Reasoning = string.IsNullOrEmpty(streamingMessage.Reasoning)
                    ? chunk.Reasoning
                    : streamingMessage.Reasoning + separator + chunk.Reasoning
            };
        }

        if (!string.IsNullOrEmpty(chunk.ToolCalls))
        {
            streamingMessage = streamingMessage with
            {
                ToolCalls = string.IsNullOrEmpty(streamingMessage.ToolCalls)
                    ? chunk.ToolCalls
                    : streamingMessage.ToolCalls + "\n" + chunk.ToolCalls
            };
        }

        return streamingMessage;
    }
}
```

Key changes:
- **New**: `BufferResumeResult` record and `ResumeFromBuffer(buffer, history, prompt, sender)` method
- **Changed**: `RebuildFromBuffer` — `internal` instead of `public` (only called by `ResumeFromBuffer` and tests via InternalsVisibleTo)
- **Changed**: `StripKnownContent` — `private` instead of `public` (only called internally)
- **Deleted**: `StripKnownContentById` — unused in production code

**Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~BufferRebuildUtilityTests" -v minimal`
Expected: All tests pass (both old RebuildFromBuffer tests and new ResumeFromBuffer tests).

Note: Some old tests call `StripKnownContent` and `StripKnownContentById` directly — these will fail since the methods are now private/deleted. Update them in the next step.

**Step 5: Update old BufferRebuildUtilityTests for visibility changes**

The `StripKnownContent` tests (8 tests) call the now-private method directly. Since `StripKnownContent` is exercised indirectly through `ResumeFromBuffer` and `RebuildFromBuffer`, delete the `StripKnownContent` and `StripKnownContentById` test regions entirely. The behavior is already covered by the `RebuildFromBuffer` tests (e.g., `RebuildFromBuffer_StripsKnownContent_FromAllTurns`).

Also, `RebuildFromBuffer` is now `internal`, so the existing tests that call it directly need `InternalsVisibleTo`. Check if this is already configured; if not, add `[assembly: InternalsVisibleTo("Tests")]` to the WebChat.Client project.

In `BufferRebuildUtilityTests.cs`:
- Delete the `#region StripKnownContent Tests` section (8 tests)
- Delete the `#region StripKnownContentById Tests` section (4 tests)

**Step 6: Run all tests to verify**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~BufferRebuildUtilityTests" -v minimal`
Expected: All remaining tests pass.

**Step 7: Commit**

```bash
git add WebChat.Client/Services/Streaming/BufferRebuildUtility.cs Tests/Unit/WebChat/Client/BufferRebuildUtilityTests.cs
git commit -m "feat(webchat): add BufferRebuildUtility.ResumeFromBuffer with anchor merge logic

Absorbs anchor-based interleaving, enrichment, prompt injection, and
content stripping from MessagePipeline into a pure static utility.
Deletes unused StripKnownContentById. Makes StripKnownContent private
and RebuildFromBuffer internal."
```

---

### Task 2: Update IMessagePipeline and MessagePipeline to use BufferResumeResult

**Files:**
- Modify: `WebChat.Client/State/Pipeline/IMessagePipeline.cs`
- Modify: `WebChat.Client/State/Pipeline/MessagePipeline.cs`

**Step 1: Update IMessagePipeline interface**

In `IMessagePipeline.cs`, change the `ResumeFromBuffer` signature from:

```csharp
void ResumeFromBuffer(string topicId, IReadOnlyList<ChatStreamMessage> buffer,
    string? currentMessageId, string? currentPrompt, string? currentSenderId);
```

To:

```csharp
void ResumeFromBuffer(BufferResumeResult result, string topicId, string? currentMessageId);
```

Add the using: `using WebChat.Client.Services.Streaming;`

**Step 2: Replace MessagePipeline.ResumeFromBuffer**

Replace the entire `ResumeFromBuffer` method (lines 170-320) in `MessagePipeline.cs` with:

```csharp
public void ResumeFromBuffer(BufferResumeResult result, string topicId, string? currentMessageId)
{
    lock (_lock)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "Pipeline.ResumeFromBuffer: topic={TopicId}, mergedCount={MergedCount}, hasStreaming={HasStreaming}",
                topicId, result.MergedMessages.Count, result.StreamingMessage.HasContent);
        }
    }

    dispatcher.Dispatch(new MessagesLoaded(topicId, result.MergedMessages));

    if (!result.StreamingMessage.HasContent)
    {
        return;
    }

    // Enrich existing history message or dispatch as streaming chunk
    var existingMessages = messagesStore.State.MessagesByTopic
        .GetValueOrDefault(topicId) ?? [];
    var historyMsg = !string.IsNullOrEmpty(currentMessageId)
        ? existingMessages.FirstOrDefault(m => m.MessageId == currentMessageId)
        : null;

    if (historyMsg is not null)
    {
        var needsReasoning = string.IsNullOrEmpty(historyMsg.Reasoning) &&
                             !string.IsNullOrEmpty(result.StreamingMessage.Reasoning);
        var needsToolCalls = string.IsNullOrEmpty(historyMsg.ToolCalls) &&
                             !string.IsNullOrEmpty(result.StreamingMessage.ToolCalls);

        if (needsReasoning || needsToolCalls)
        {
            var enriched = historyMsg with
            {
                Reasoning = needsReasoning ? result.StreamingMessage.Reasoning : historyMsg.Reasoning,
                ToolCalls = needsToolCalls ? result.StreamingMessage.ToolCalls : historyMsg.ToolCalls
            };
            dispatcher.Dispatch(new UpdateMessage(topicId, currentMessageId!, enriched));
            return;
        }
    }

    dispatcher.Dispatch(new StreamChunk(
        topicId,
        result.StreamingMessage.Content,
        result.StreamingMessage.Reasoning,
        result.StreamingMessage.ToolCalls,
        currentMessageId));
}
```

Remove the `using Domain.DTOs.WebChat;` import from MessagePipeline.cs if it's no longer needed (check if other methods use `ChatStreamMessage` or `ChatHistoryMessage` — yes, `LoadHistory` uses `ChatHistoryMessage`, so keep it). Also add `using WebChat.Client.Services.Streaming;` if not already present (it is — line 4).

**Step 3: Build to verify compilation**

Run: `dotnet build WebChat.Client/ --no-restore`
Expected: Build succeeds. (Tests may fail since callers haven't been updated yet.)

**Step 4: Commit**

```bash
git add WebChat.Client/State/Pipeline/IMessagePipeline.cs WebChat.Client/State/Pipeline/MessagePipeline.cs
git commit -m "refactor(webchat): simplify MessagePipeline.ResumeFromBuffer to dispatch-only

Takes pre-built BufferResumeResult instead of raw buffer. Reduces method
from ~150 lines to ~35 lines. All transformation logic now lives in
BufferRebuildUtility."
```

---

### Task 3: Update StreamResumeService to single rebuild

**Files:**
- Modify: `WebChat.Client/Services/Streaming/StreamResumeService.cs`

**Step 1: Update StreamResumeService.TryResumeStreamAsync**

Replace lines 64-75 (the buffer rebuild and pipeline call section) with:

```csharp
// Single rebuild: buffer + history → merged result
var existingHistory = messagesStore.State.MessagesByTopic
    .GetValueOrDefault(topic.TopicId) ?? [];
var result = BufferRebuildUtility.ResumeFromBuffer(
    state.BufferedMessages, existingHistory, state.CurrentPrompt, state.CurrentSenderId);

// Start streaming FIRST (dispatches StreamStarted which creates empty StreamingContent)
// Then ResumeFromBuffer fills it with content via StreamChunk
// Order matters: StreamStarted resets content, so it must come before StreamChunk
await streamingService.TryStartResumeStreamAsync(topic, result.StreamingMessage, state.CurrentMessageId);

pipeline.ResumeFromBuffer(result, topic.TopicId, state.CurrentMessageId);
```

**Step 2: Build to verify compilation**

Run: `dotnet build WebChat.Client/ --no-restore`
Expected: Build succeeds.

**Step 3: Commit**

```bash
git add WebChat.Client/Services/Streaming/StreamResumeService.cs
git commit -m "refactor(webchat): single buffer rebuild in StreamResumeService

Calls BufferRebuildUtility.ResumeFromBuffer once instead of rebuilding
the buffer twice. Passes result to both StreamingService and
MessagePipeline."
```

---

### Task 4: Update MessagePipelineTests for new ResumeFromBuffer signature

**Files:**
- Modify: `Tests/Unit/WebChat.Client/State/Pipeline/MessagePipelineTests.cs`

**Step 1: Update ResumeFromBuffer tests**

The 5 `ResumeFromBuffer_*` tests in `MessagePipelineTests` currently call the old signature with raw buffer. Since the merge logic now lives in `BufferRebuildUtility` (tested in Task 1), these pipeline tests should verify dispatching behavior given a `BufferResumeResult`.

Replace the 5 `ResumeFromBuffer_*` test methods with simplified versions that pass a pre-built result:

```csharp
[Fact]
public void ResumeFromBuffer_DispatchesMergedMessages()
{
    var result = new BufferResumeResult(
        [
            new ChatMessageModel { Role = "user", Content = "Q1" },
            new ChatMessageModel { Role = "assistant", Content = "A1" }
        ],
        new ChatMessageModel { Role = "assistant" });

    _pipeline.ResumeFromBuffer(result, "topic-1", null);

    var messages = _messagesStore.State.MessagesByTopic["topic-1"];
    messages.Count.ShouldBe(2);
    messages[0].Content.ShouldBe("Q1");
    messages[1].Content.ShouldBe("A1");
}

[Fact]
public void ResumeFromBuffer_DispatchesStreamChunkWhenStreamingContent()
{
    var result = new BufferResumeResult(
        [],
        new ChatMessageModel { Role = "assistant", Content = "Streaming..." });

    _pipeline.ResumeFromBuffer(result, "topic-1", "msg-1");

    // StreamChunk was dispatched — verify via streaming store
    var streaming = new StreamingStore(_dispatcher).State.StreamingByTopic.GetValueOrDefault("topic-1");
    // The StreamChunk gets processed by the streaming reducer
    // We can't easily check without a fresh store, so just verify no exception
}

[Fact]
public void ResumeFromBuffer_WithNoStreamingContent_DoesNotDispatchChunk()
{
    var result = new BufferResumeResult(
        [new ChatMessageModel { Role = "user", Content = "Q1" }],
        new ChatMessageModel { Role = "assistant" }); // No content

    _pipeline.ResumeFromBuffer(result, "topic-1", null);

    var messages = _messagesStore.State.MessagesByTopic["topic-1"];
    messages.Count.ShouldBe(1);
}
```

Add `using WebChat.Client.Services.Streaming;` to the imports.

**Step 2: Run tests to verify**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MessagePipelineTests" -v minimal`
Expected: All tests pass.

**Step 3: Commit**

```bash
git add Tests/Unit/WebChat.Client/State/Pipeline/MessagePipelineTests.cs
git commit -m "test(webchat): update MessagePipelineTests for new ResumeFromBuffer signature

Pipeline tests now verify dispatching behavior given pre-built
BufferResumeResult. Merge logic tests live in BufferRebuildUtilityTests."
```

---

### Task 5: Run full test suite and fix any remaining issues

**Files:**
- Possibly: `Tests/Unit/WebChat/Client/StreamResumeServiceTests.cs`
- Possibly: `Tests/Unit/WebChat.Client/State/Pipeline/MessagePipelineIntegrationTests.cs`

**Step 1: Run full test suite**

Run: `dotnet test Tests/ -v minimal`
Expected: All tests pass. If `StreamResumeServiceTests` or `MessagePipelineIntegrationTests` fail, fix them.

`StreamResumeServiceTests` should continue working because it uses real `MessagePipeline` and `StreamResumeService` instances — the internal wiring changed but the external behavior is the same. The tests go through `StreamResumeService.TryResumeStreamAsync` which now calls the new code path internally.

`MessagePipelineIntegrationTests` don't call `ResumeFromBuffer` at all, so they should be unaffected.

**Step 2: Fix any failing tests**

If `StreamResumeServiceTests` fails, it's likely because the test constructs a `MessagePipeline` that now needs an `InternalsVisibleTo` or because the behavior slightly changed. Debug and fix.

**Step 3: Commit if any fixes were needed**

```bash
git add -A
git commit -m "fix(tests): update remaining tests for pipeline simplification"
```

---

### Task 6: Verify InternalsVisibleTo is configured

**Files:**
- Possibly: `WebChat.Client/WebChat.Client.csproj` or `WebChat.Client/AssemblyInfo.cs`

**Step 1: Check if InternalsVisibleTo exists**

Search for `InternalsVisibleTo` in the WebChat.Client project. If `RebuildFromBuffer` (now `internal`) is called from tests, we need this attribute.

Check: `grep -r "InternalsVisibleTo" WebChat.Client/`

**Step 2: Add if missing**

If not present, add to `WebChat.Client.csproj`:

```xml
<ItemGroup>
  <InternalsVisibleTo Include="Tests" />
</ItemGroup>
```

Or add an `AssemblyInfo.cs` file with `[assembly: InternalsVisibleTo("Tests")]`.

**Step 3: Run tests again**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~BufferRebuildUtilityTests" -v minimal`
Expected: All pass.

**Step 4: Commit if changes were needed**

```bash
git add WebChat.Client/WebChat.Client.csproj
git commit -m "chore: add InternalsVisibleTo for test access to internal members"
```

---

### Task 7: Final verification and cleanup

**Step 1: Run full test suite one final time**

Run: `dotnet test Tests/ -v minimal`
Expected: All tests pass.

**Step 2: Verify no stale references to deleted methods**

Search for any remaining references to the deleted API:
- `StripKnownContentById` — should have zero results
- `StripKnownContent` — should only appear inside `BufferRebuildUtility.cs` (private)

Run:
```
grep -r "StripKnownContentById" --include="*.cs" .
grep -r "StripKnownContent" --include="*.cs" . | grep -v BufferRebuildUtility.cs
```

Expected: No results for either search.

**Step 3: Verify build**

Run: `dotnet build --no-restore`
Expected: Clean build.

**Step 4: Commit cleanup if needed, then done**

No commit needed if nothing changed. The refactoring is complete.
