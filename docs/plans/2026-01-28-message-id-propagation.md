# MessageId Propagation Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Wire MessageId through the frontend to enable precise deduplication and simplify content-matching logic during stream resume.

**Architecture:** Add MessageId to ChatMessageModel, propagate from ChatHistoryMessage through all mapping sites, update MessagesLoaded reducer to populate FinalizedMessageIdsByTopic, and refactor BufferRebuildUtility to use ID-based content matching instead of scanning all history.

**Tech Stack:** Blazor WebAssembly, C# records, Redux-like state management

---

## Task 1: Add MessageId to ChatMessageModel

**Files:**
- Modify: `WebChat.Client/Models/ChatMessageModel.cs:3-18`
- Test: `Tests/Unit/WebChat/Client/ChatMessageModelTests.cs` (new)

**Step 1: Write the failing test**

Create `Tests/Unit/WebChat/Client/ChatMessageModelTests.cs`:

```csharp
using Shouldly;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client;

public sealed class ChatMessageModelTests
{
    [Fact]
    public void ChatMessageModel_HasMessageIdProperty()
    {
        var message = new ChatMessageModel { MessageId = "msg-123" };
        message.MessageId.ShouldBe("msg-123");
    }

    [Fact]
    public void ChatMessageModel_MessageIdDefaultsToNull()
    {
        var message = new ChatMessageModel();
        message.MessageId.ShouldBeNull();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatMessageModelTests" --no-build`
Expected: FAIL with "ChatMessageModel does not contain a definition for MessageId"

**Step 3: Add MessageId property to ChatMessageModel**

In `WebChat.Client/Models/ChatMessageModel.cs`, add after line 12 (`Timestamp` property):

```csharp
    public string? MessageId { get; init; }
```

**Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatMessageModelTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add WebChat.Client/Models/ChatMessageModel.cs Tests/Unit/WebChat/Client/ChatMessageModelTests.cs
git commit -m "$(cat <<'EOF'
feat(webchat): add MessageId property to ChatMessageModel

Enables message identity tracking for deduplication and future
UpdateMessage functionality.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Create Mapping Helper for ChatHistoryMessage

**Files:**
- Create: `WebChat.Client/Extensions/ChatHistoryMessageExtensions.cs`
- Test: `Tests/Unit/WebChat/Client/ChatHistoryMessageExtensionsTests.cs` (new)

**Step 1: Write the failing test**

Create `Tests/Unit/WebChat/Client/ChatHistoryMessageExtensionsTests.cs`:

```csharp
using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.Extensions;

namespace Tests.Unit.WebChat.Client;

public sealed class ChatHistoryMessageExtensionsTests
{
    [Fact]
    public void ToChatMessageModel_MapsAllProperties()
    {
        var history = new ChatHistoryMessage(
            MessageId: "msg-123",
            Role: "assistant",
            Content: "Hello",
            SenderId: "agent-1",
            Timestamp: new DateTimeOffset(2026, 1, 28, 12, 0, 0, TimeSpan.Zero));

        var result = history.ToChatMessageModel();

        result.MessageId.ShouldBe("msg-123");
        result.Role.ShouldBe("assistant");
        result.Content.ShouldBe("Hello");
        result.SenderId.ShouldBe("agent-1");
        result.Timestamp.ShouldBe(new DateTimeOffset(2026, 1, 28, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ToChatMessageModel_HandlesNullMessageId()
    {
        var history = new ChatHistoryMessage(null, "user", "Hi", null, null);

        var result = history.ToChatMessageModel();

        result.MessageId.ShouldBeNull();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatHistoryMessageExtensionsTests" --no-build`
Expected: FAIL with "ChatHistoryMessageExtensions does not exist"

**Step 3: Create the extension method**

Create `WebChat.Client/Extensions/ChatHistoryMessageExtensions.cs`:

```csharp
using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.Extensions;

public static class ChatHistoryMessageExtensions
{
    public static ChatMessageModel ToChatMessageModel(this ChatHistoryMessage history) =>
        new()
        {
            MessageId = history.MessageId,
            Role = history.Role,
            Content = history.Content,
            SenderId = history.SenderId,
            Timestamp = history.Timestamp
        };
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatHistoryMessageExtensionsTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add WebChat.Client/Extensions/ChatHistoryMessageExtensions.cs Tests/Unit/WebChat/Client/ChatHistoryMessageExtensionsTests.cs
git commit -m "$(cat <<'EOF'
feat(webchat): add ChatHistoryMessage to ChatMessageModel extension

Centralizes mapping logic and ensures MessageId is propagated.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Update All History Mapping Sites

**Files:**
- Modify: `WebChat.Client/State/Hub/ReconnectionEffect.cs:92-98`
- Modify: `WebChat.Client/State/Effects/TopicSelectionEffect.cs:64-70`
- Modify: `WebChat.Client/State/Effects/AgentSelectionEffect.cs:71-77`
- Modify: `WebChat.Client/State/Effects/InitializationEffect.cs:109-115`
- Modify: `WebChat.Client/Services/Streaming/StreamResumeService.cs:52-58`

**Step 1: Update ReconnectionEffect.cs**

Replace lines 91-99 in `ReconnectionEffect.cs`:

```csharp
    private async Task ReloadTopicHistoryAsync(StoredTopic topic)
    {
        var history = await _topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);
        var messages = history.Select(h => h.ToChatMessageModel()).ToList();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, messages));
    }
```

Add using at top: `using WebChat.Client.Extensions;`

**Step 2: Update TopicSelectionEffect.cs**

Replace lines 64-70 in `TopicSelectionEffect.cs`:

```csharp
                var messages = history.Select(h => h.ToChatMessageModel()).ToList();
```

Add using at top: `using WebChat.Client.Extensions;`

**Step 3: Update AgentSelectionEffect.cs**

Replace lines 71-77 in `AgentSelectionEffect.cs`:

```csharp
        var messages = history.Select(h => h.ToChatMessageModel()).ToList();
```

Add using at top: `using WebChat.Client.Extensions;`

**Step 4: Update InitializationEffect.cs**

Replace lines 109-115 in `InitializationEffect.cs`:

```csharp
        var messages = history.Select(h => h.ToChatMessageModel()).ToList();
```

Add using at top: `using WebChat.Client.Extensions;`

**Step 5: Update StreamResumeService.cs**

Replace lines 52-58 in `StreamResumeService.cs`:

```csharp
                var messages = history.Select(h => h.ToChatMessageModel()).ToList();
```

Add using at top: `using WebChat.Client.Extensions;`

**Step 6: Verify all compile**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: Build succeeded

**Step 7: Commit**

```bash
git add WebChat.Client/State/Hub/ReconnectionEffect.cs WebChat.Client/State/Effects/TopicSelectionEffect.cs WebChat.Client/State/Effects/AgentSelectionEffect.cs WebChat.Client/State/Effects/InitializationEffect.cs WebChat.Client/Services/Streaming/StreamResumeService.cs
git commit -m "$(cat <<'EOF'
refactor(webchat): use ToChatMessageModel extension in all mapping sites

Replaces 5 duplicated inline mappings with extension method.
MessageId now propagates from history to frontend.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Update MessagesLoaded Reducer to Populate FinalizedMessageIds

**Files:**
- Modify: `WebChat.Client/State/Messages/MessagesReducers.cs:9-16`
- Test: `Tests/Unit/WebChat/Client/MessagesReducersTests.cs` (new)

**Step 1: Write the failing test**

Create `Tests/Unit/WebChat/Client/MessagesReducersTests.cs`:

```csharp
using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.State.Messages;

namespace Tests.Unit.WebChat.Client;

public sealed class MessagesReducersTests
{
    [Fact]
    public void MessagesLoaded_PopulatesFinalizedMessageIds()
    {
        var state = MessagesState.Initial;
        var messages = new List<ChatMessageModel>
        {
            new() { MessageId = "msg-1", Role = "user", Content = "Hello" },
            new() { MessageId = "msg-2", Role = "assistant", Content = "Hi" },
            new() { MessageId = null, Role = "user", Content = "No ID" }
        };

        var newState = MessagesReducers.Reduce(state, new MessagesLoaded("topic-1", messages));

        newState.FinalizedMessageIdsByTopic.ShouldContainKey("topic-1");
        var finalizedIds = newState.FinalizedMessageIdsByTopic["topic-1"];
        finalizedIds.ShouldContain("msg-1");
        finalizedIds.ShouldContain("msg-2");
        finalizedIds.Count.ShouldBe(2); // null MessageId not included
    }

    [Fact]
    public void MessagesLoaded_WithNoMessageIds_CreatesEmptySet()
    {
        var state = MessagesState.Initial;
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Hello" }
        };

        var newState = MessagesReducers.Reduce(state, new MessagesLoaded("topic-1", messages));

        newState.FinalizedMessageIdsByTopic.ShouldContainKey("topic-1");
        newState.FinalizedMessageIdsByTopic["topic-1"].ShouldBeEmpty();
    }

    [Fact]
    public void AddMessage_SkipsIfMessageIdAlreadyFinalized()
    {
        var state = MessagesState.Initial with
        {
            MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>
            {
                ["topic-1"] = new List<ChatMessageModel>
                {
                    new() { MessageId = "msg-1", Role = "assistant", Content = "Existing" }
                }
            },
            FinalizedMessageIdsByTopic = new Dictionary<string, IReadOnlySet<string>>
            {
                ["topic-1"] = new HashSet<string> { "msg-1" }
            }
        };

        var newMessage = new ChatMessageModel { MessageId = "msg-1", Role = "assistant", Content = "Duplicate" };
        var newState = MessagesReducers.Reduce(state, new AddMessage("topic-1", newMessage, "msg-1"));

        newState.MessagesByTopic["topic-1"].Count.ShouldBe(1); // Not added
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessagesReducersTests.MessagesLoaded_PopulatesFinalizedMessageIds"`
Expected: FAIL - FinalizedMessageIdsByTopic not populated by MessagesLoaded

**Step 3: Update MessagesLoaded reducer**

Replace the `MessagesLoaded` case in `MessagesReducers.cs` (lines 9-16):

```csharp
        MessagesLoaded a => state with
        {
            MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>(state.MessagesByTopic)
            {
                [a.TopicId] = a.Messages
            },
            LoadedTopics = new HashSet<string>(state.LoadedTopics) { a.TopicId },
            FinalizedMessageIdsByTopic = new Dictionary<string, IReadOnlySet<string>>(state.FinalizedMessageIdsByTopic)
            {
                [a.TopicId] = a.Messages
                    .Select(m => m.MessageId)
                    .Where(id => id is not null)
                    .ToHashSet()!
            }
        },
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessagesReducersTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add WebChat.Client/State/Messages/MessagesReducers.cs Tests/Unit/WebChat/Client/MessagesReducersTests.cs
git commit -m "$(cat <<'EOF'
feat(webchat): populate FinalizedMessageIdsByTopic on MessagesLoaded

Enables deduplication when resuming streams - streamed messages with
IDs already in history will be skipped.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Refactor BufferRebuildUtility to Use ID-Based Matching

**Files:**
- Modify: `WebChat.Client/Services/Streaming/BufferRebuildUtility.cs`
- Modify: `Tests/Unit/WebChat/Client/BufferRebuildUtilityTests.cs`

**Step 1: Write test for new ID-based StripKnownContent**

Add to `BufferRebuildUtilityTests.cs`:

```csharp
    #region StripKnownContentById Tests

    [Fact]
    public void StripKnownContentById_WhenIdNotInHistory_ReturnsUnchanged()
    {
        var message = new ChatMessageModel { Role = "assistant", Content = "New content" };
        var historyById = new Dictionary<string, string> { ["msg-1"] = "Old content" };

        var result = BufferRebuildUtility.StripKnownContentById(message, "msg-2", historyById);

        result.Content.ShouldBe("New content");
    }

    [Fact]
    public void StripKnownContentById_WhenBufferIsSubset_ReturnsEmpty()
    {
        var message = new ChatMessageModel { Role = "assistant", Content = "partial", Reasoning = "thinking" };
        var historyById = new Dictionary<string, string> { ["msg-1"] = "partial content complete" };

        var result = BufferRebuildUtility.StripKnownContentById(message, "msg-1", historyById);

        result.Content.ShouldBeEmpty();
        result.Reasoning.ShouldBeNull();
    }

    [Fact]
    public void StripKnownContentById_WhenBufferHasMore_StripsPrefix()
    {
        var message = new ChatMessageModel { Role = "assistant", Content = "Known new stuff" };
        var historyById = new Dictionary<string, string> { ["msg-1"] = "Known" };

        var result = BufferRebuildUtility.StripKnownContentById(message, "msg-1", historyById);

        result.Content.ShouldBe("new stuff");
    }

    [Fact]
    public void StripKnownContentById_WithNullMessageId_ReturnsUnchanged()
    {
        var message = new ChatMessageModel { Role = "assistant", Content = "Content" };
        var historyById = new Dictionary<string, string> { ["msg-1"] = "Content" };

        var result = BufferRebuildUtility.StripKnownContentById(message, null, historyById);

        result.Content.ShouldBe("Content");
    }

    #endregion
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~StripKnownContentById"`
Expected: FAIL - method doesn't exist

**Step 3: Add StripKnownContentById method**

Add to `BufferRebuildUtility.cs` after `StripKnownContent`:

```csharp
    public static ChatMessageModel StripKnownContentById(
        ChatMessageModel message,
        string? messageId,
        IReadOnlyDictionary<string, string> historyContentById)
    {
        if (string.IsNullOrEmpty(message.Content) ||
            string.IsNullOrEmpty(messageId) ||
            !historyContentById.TryGetValue(messageId, out var knownContent))
        {
            return message;
        }

        // Buffer content is subset of history - entire message already saved
        if (knownContent.Contains(message.Content))
        {
            return message with { Content = "", Reasoning = null };
        }

        // Buffer has more than history - strip the known prefix
        if (message.Content.StartsWith(knownContent))
        {
            return message with { Content = message.Content[knownContent.Length..].TrimStart() };
        }

        return message;
    }
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~StripKnownContentById"`
Expected: PASS

**Step 5: Commit**

```bash
git add WebChat.Client/Services/Streaming/BufferRebuildUtility.cs Tests/Unit/WebChat/Client/BufferRebuildUtilityTests.cs
git commit -m "$(cat <<'EOF'
feat(webchat): add StripKnownContentById for precise content matching

O(1) lookup by MessageId instead of O(n) scan of all history content.
Prevents false positives from similar content in different messages.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Update StreamResumeService to Use ID-Based Matching

**Files:**
- Modify: `WebChat.Client/Services/Streaming/StreamResumeService.cs`
- Modify: `Tests/Unit/WebChat/Client/StreamResumeServiceTests.cs`

**Step 1: Update StreamResumeService to build historyContentById**

Replace lines 87-97 in `StreamResumeService.cs`:

```csharp
            // Re-read after potential AddMessage dispatch
            existingMessages = messagesStore.State.MessagesByTopic
                .GetValueOrDefault(topic.TopicId) ?? [];

            // Build ID-based lookup for precise content matching
            var historyContentById = existingMessages
                .Where(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Content) && !string.IsNullOrEmpty(m.MessageId))
                .ToDictionary(m => m.MessageId!, m => m.Content);

            // Keep HashSet for backward compatibility with RebuildFromBuffer
            var historyContent = historyContentById.Values.ToHashSet();

            var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(
                state.BufferedMessages, historyContent);
```

**Step 2: Update StripKnownContent call to use ID-based version**

Replace line 106 in `StreamResumeService.cs`:

```csharp
            streamingMessage = BufferRebuildUtility.StripKnownContentById(
                streamingMessage, state.CurrentMessageId, historyContentById);
```

**Step 3: Replace prompt content check with MessageId check**

Replace lines 65-79 in `StreamResumeService.cs`:

```csharp
            if (!string.IsNullOrEmpty(state.CurrentPrompt))
            {
                var userMessageIds = messagesStore.State.FinalizedMessageIdsByTopic
                    .GetValueOrDefault(topic.TopicId) ?? new HashSet<string>();

                var promptMessageId = state.CurrentUserMessageId;
                var promptExists = !string.IsNullOrEmpty(promptMessageId) && userMessageIds.Contains(promptMessageId);

                // Fallback to content check if no message ID available
                if (!promptExists && string.IsNullOrEmpty(promptMessageId))
                {
                    promptExists = existingMessages.Any(m =>
                        m.Role == "user" && m.Content == state.CurrentPrompt);
                }

                if (!promptExists)
                {
                    dispatcher.Dispatch(new AddMessage(topic.TopicId, new ChatMessageModel
                    {
                        MessageId = promptMessageId,
                        Role = "user",
                        Content = state.CurrentPrompt,
                        SenderId = state.CurrentSenderId
                    }, promptMessageId));
                }
            }
```

Note: This requires `CurrentUserMessageId` to be added to StreamState. If not available, the fallback content check remains.

**Step 4: Verify build succeeds**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: Build succeeded

**Step 5: Run existing StreamResumeService tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~StreamResumeService"`
Expected: PASS

**Step 6: Commit**

```bash
git add WebChat.Client/Services/Streaming/StreamResumeService.cs
git commit -m "$(cat <<'EOF'
refactor(webchat): use ID-based content matching in StreamResumeService

Uses StripKnownContentById for O(1) lookup instead of scanning.
Falls back to content matching when MessageId unavailable.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Update RebuildFromBuffer to Pass MessageId Through

**Files:**
- Modify: `WebChat.Client/Services/Streaming/BufferRebuildUtility.cs`
- Modify: `Tests/Unit/WebChat/Client/BufferRebuildUtilityTests.cs`

**Step 1: Write test for MessageId propagation in completed turns**

Add to `BufferRebuildUtilityTests.cs`:

```csharp
    [Fact]
    public void RebuildFromBuffer_PropagatesMessageIdToCompletedTurns()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "First", MessageId = "msg-1" },
            new() { IsComplete = true, MessageId = "msg-1" },
            new() { Content = "Second", MessageId = "msg-2" }
        };

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer, []);

        completedTurns[0].MessageId.ShouldBe("msg-1");
        // Note: streamingMessage.MessageId is not set - that's handled by StreamingService
    }

    [Fact]
    public void RebuildFromBuffer_UserMessages_HaveNoMessageId()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "Hello", UserMessage = new UserMessageInfo("alice", null) }
        };

        var (completedTurns, _) = BufferRebuildUtility.RebuildFromBuffer(buffer, []);

        completedTurns[0].MessageId.ShouldBeNull(); // User messages from buffer don't have IDs
    }
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~PropagatesMessageIdToCompletedTurns"`
Expected: FAIL - MessageId is null

**Step 3: Update RebuildFromBuffer to track and assign MessageId**

In `BufferRebuildUtility.cs`, update the completed turn creation. Replace lines 36-44:

```csharp
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
```

Similarly update lines 60-68 and 75-85 to include `MessageId = currentMessageId` when adding to completedTurns.

**Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~BufferRebuildUtilityTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add WebChat.Client/Services/Streaming/BufferRebuildUtility.cs Tests/Unit/WebChat/Client/BufferRebuildUtilityTests.cs
git commit -m "$(cat <<'EOF'
feat(webchat): propagate MessageId through RebuildFromBuffer

Completed assistant turns now carry their MessageId for deduplication.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: Implement UpdateMessageInTopic with MessageId

**Files:**
- Modify: `WebChat.Client/State/Messages/MessagesReducers.cs:92-111`
- Modify: `Tests/Unit/WebChat/Client/MessagesReducersTests.cs`

**Step 1: Write failing test for UpdateMessage**

Add to `MessagesReducersTests.cs`:

```csharp
    [Fact]
    public void UpdateMessage_FindsAndUpdatesMessageById()
    {
        var state = MessagesState.Initial with
        {
            MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>
            {
                ["topic-1"] = new List<ChatMessageModel>
                {
                    new() { MessageId = "msg-1", Role = "assistant", Content = "Original" },
                    new() { MessageId = "msg-2", Role = "assistant", Content = "Other" }
                }
            }
        };

        var updated = new ChatMessageModel { MessageId = "msg-1", Role = "assistant", Content = "Updated" };
        var newState = MessagesReducers.Reduce(state, new UpdateMessage("topic-1", "msg-1", updated));

        var messages = newState.MessagesByTopic["topic-1"];
        messages[0].Content.ShouldBe("Updated");
        messages[1].Content.ShouldBe("Other"); // Unchanged
    }

    [Fact]
    public void UpdateMessage_WhenMessageIdNotFound_NoChange()
    {
        var state = MessagesState.Initial with
        {
            MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>
            {
                ["topic-1"] = new List<ChatMessageModel>
                {
                    new() { MessageId = "msg-1", Role = "assistant", Content = "Original" }
                }
            }
        };

        var updated = new ChatMessageModel { MessageId = "msg-99", Role = "assistant", Content = "Updated" };
        var newState = MessagesReducers.Reduce(state, new UpdateMessage("topic-1", "msg-99", updated));

        newState.MessagesByTopic["topic-1"][0].Content.ShouldBe("Original");
    }
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~UpdateMessage_FindsAndUpdatesMessageById"`
Expected: FAIL - message not updated (no-op implementation)

**Step 3: Implement UpdateMessageInTopic**

Replace `UpdateMessageInTopic` method in `MessagesReducers.cs`:

```csharp
    private static IReadOnlyDictionary<string, IReadOnlyList<ChatMessageModel>> UpdateMessageInTopic(
        IReadOnlyDictionary<string, IReadOnlyList<ChatMessageModel>> messagesByTopic,
        string topicId,
        string messageId,
        ChatMessageModel updatedMessage)
    {
        if (!messagesByTopic.TryGetValue(topicId, out var messages))
        {
            return messagesByTopic;
        }

        var updated = messages
            .Select(m => m.MessageId == messageId ? updatedMessage : m)
            .ToList();

        // If no message was updated, return unchanged
        if (updated.SequenceEqual(messages))
        {
            return messagesByTopic;
        }

        return new Dictionary<string, IReadOnlyList<ChatMessageModel>>(messagesByTopic)
        {
            [topicId] = updated
        };
    }
```

Remove the ReSharper disable comments.

**Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessagesReducersTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add WebChat.Client/State/Messages/MessagesReducers.cs Tests/Unit/WebChat/Client/MessagesReducersTests.cs
git commit -m "$(cat <<'EOF'
feat(webchat): implement UpdateMessageInTopic with MessageId lookup

Enables updating specific messages by ID instead of position.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: Run Full Test Suite and Verify

**Files:**
- None (verification only)

**Step 1: Run all WebChat.Client tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WebChat.Client"`
Expected: All tests PASS

**Step 2: Run integration tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~StreamResumeServiceIntegration"`
Expected: All tests PASS

**Step 3: Build entire solution**

Run: `dotnet build`
Expected: Build succeeded with no warnings related to MessageId

**Step 4: Commit (if any fixes needed)**

Only if fixes were required in previous steps.

---

## Summary

After completing all tasks:

1. **ChatMessageModel** has `MessageId` property
2. **All 5 mapping sites** use `ToChatMessageModel()` extension that propagates MessageId
3. **MessagesLoaded reducer** populates `FinalizedMessageIdsByTopic` from loaded history
4. **BufferRebuildUtility** has `StripKnownContentById` for O(1) ID-based matching
5. **StreamResumeService** uses ID-based matching with content fallback
6. **RebuildFromBuffer** propagates MessageId to completed turns
7. **UpdateMessageInTopic** properly finds and updates messages by ID

This enables:
- Precise deduplication when resuming streams after reconnection
- No false positives from similar content in different messages
- Future message editing/updating by ID
