using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;

namespace Tests.Unit.WebChat.Client;

public sealed class BufferRebuildUtilityTests
{
    #region RebuildFromBuffer Tests

    [Fact]
    public void RebuildFromBuffer_WithEmptyBuffer_ReturnsEmptyMessage()
    {
        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer([]);

        completedTurns.ShouldBeEmpty();
        streamingMessage.Role.ShouldBe("assistant");
        streamingMessage.Content.ShouldBeEmpty();
    }

    [Fact]
    public void RebuildFromBuffer_WithSingleTurn_ReturnsStreamingMessage()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "Hello", MessageId = "msg-1" },
            new() { Content = " world", MessageId = "msg-1" }
        };

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        completedTurns.ShouldBeEmpty();
        streamingMessage.Content.ShouldBe("Hello world");
    }

    [Fact]
    public void RebuildFromBuffer_WithCompleteFlag_MovesToCompletedTurns()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "First turn", MessageId = "msg-1" },
            new() { IsComplete = true, MessageId = "msg-1" }
        };

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        completedTurns.Count.ShouldBe(1);
        completedTurns[0].Content.ShouldBe("First turn");
        streamingMessage.Content.ShouldBeEmpty();
    }

    [Fact]
    public void RebuildFromBuffer_WithMultipleTurns_SeparatesCompleted()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "First", MessageId = "msg-1" },
            new() { IsComplete = true, MessageId = "msg-1" },
            new() { Content = "Second", MessageId = "msg-2" }
        };

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        completedTurns.Count.ShouldBe(1);
        completedTurns[0].Content.ShouldBe("First");
        streamingMessage.Content.ShouldBe("Second");
    }

    [Fact]
    public void RebuildFromBuffer_GroupsByMessageId_PreservesOrder()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "A1", MessageId = "msg-a" },
            new() { Content = "A2", MessageId = "msg-a" },
            new() { IsComplete = true, MessageId = "msg-a" },
            new() { Content = "B1", MessageId = "msg-b" },
            new() { Content = "B2", MessageId = "msg-b" }
        };

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        completedTurns.Count.ShouldBe(1);
        completedTurns[0].Content.ShouldBe("A1A2");
        streamingMessage.Content.ShouldBe("B1B2");
    }

    [Fact]
    public void RebuildFromBuffer_WithReasoning_AccumulatesCorrectly()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Reasoning = "Thinking...", MessageId = "msg-1" },
            new() { Content = "Answer", MessageId = "msg-1" }
        };

        var (_, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        streamingMessage.Reasoning.ShouldBe("Thinking...");
        streamingMessage.Content.ShouldBe("Answer");
    }

    [Fact]
    public void RebuildFromBuffer_WithToolCalls_AccumulatesWithNewlines()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { ToolCalls = "tool_1", MessageId = "msg-1" },
            new() { ToolCalls = "tool_2", MessageId = "msg-1" }
        };

        var (_, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        streamingMessage.ToolCalls.ShouldBe("tool_1\ntool_2");
    }

    [Fact]
    public void RebuildFromBuffer_SkipsEmptyCompletedTurns()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { IsComplete = true, MessageId = "msg-1" },
            new() { Content = "Second turn", MessageId = "msg-2" }
        };

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        completedTurns.ShouldBeEmpty();
        streamingMessage.Content.ShouldBe("Second turn");
    }

    [Fact]
    public void RebuildFromBuffer_WithUserMessage_IncludesInCompletedTurns()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "Hello from user", UserMessage = new UserMessageInfo("alice", null) },
            new() { Content = "Hi there!", MessageId = "msg-1" }
        };

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        completedTurns.Count.ShouldBe(1);
        completedTurns[0].Role.ShouldBe("user");
        completedTurns[0].Content.ShouldBe("Hello from user");
        completedTurns[0].SenderId.ShouldBe("alice");
        streamingMessage.Content.ShouldBe("Hi there!");
    }

    [Fact]
    public void RebuildFromBuffer_WithMixedMessages_PreservesChronologicalOrder()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "User msg 1", UserMessage = new UserMessageInfo("alice", null), SequenceNumber = 1 },
            new() { Content = "Assistant response 1", MessageId = "msg-1", SequenceNumber = 2 },
            new() { IsComplete = true, MessageId = "msg-1", SequenceNumber = 3 },
            new() { Content = "User msg 2", UserMessage = new UserMessageInfo("bob", null), SequenceNumber = 4 },
            new() { Content = "Assistant response 2", MessageId = "msg-2", SequenceNumber = 5 }
        };

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        completedTurns.Count.ShouldBe(3);
        completedTurns[0].Role.ShouldBe("user");
        completedTurns[0].Content.ShouldBe("User msg 1");
        completedTurns[1].Role.ShouldBe("assistant");
        completedTurns[1].Content.ShouldBe("Assistant response 1");
        completedTurns[2].Role.ShouldBe("user");
        completedTurns[2].Content.ShouldBe("User msg 2");
        streamingMessage.Content.ShouldBe("Assistant response 2");
    }

    [Fact]
    public void RebuildFromBuffer_PropagatesMessageIdToCompletedTurns()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "First", MessageId = "msg-1" },
            new() { IsComplete = true, MessageId = "msg-1" },
            new() { Content = "Second", MessageId = "msg-2" }
        };

        var (completedTurns, _) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        completedTurns[0].MessageId.ShouldBe("msg-1");
    }

    [Fact]
    public void RebuildFromBuffer_UserMessages_HaveNoMessageId()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "Hello", UserMessage = new UserMessageInfo("alice", null) }
        };

        var (completedTurns, _) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        completedTurns[0].MessageId.ShouldBeNull();
    }

    [Fact]
    public void RebuildFromBuffer_WithTimestamp_CarriesTimestampForward()
    {
        var ts = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "Hello", MessageId = "msg-1", Timestamp = ts },
            new() { Content = " world", MessageId = "msg-1" }
        };

        var (_, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        streamingMessage.Timestamp.ShouldBe(ts);
    }

    [Fact]
    public void RebuildFromBuffer_WithMultipleTimestamps_LastWins()
    {
        var ts1 = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var ts2 = new DateTimeOffset(2025, 6, 15, 12, 0, 5, TimeSpan.Zero);
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "Hello", MessageId = "msg-1", Timestamp = ts1 },
            new() { Content = " world", MessageId = "msg-1", Timestamp = ts2 }
        };

        var (_, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        streamingMessage.Timestamp.ShouldBe(ts2);
    }

    [Fact]
    public void RebuildFromBuffer_WithoutTimestamp_RemainsNull()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "Hello", MessageId = "msg-1" }
        };

        var (_, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        streamingMessage.Timestamp.ShouldBeNull();
    }

    #endregion

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
            new()
            {
                MessageId = "msg-1", Content = "A1", Reasoning = "Thought process", IsComplete = true,
                SequenceNumber = 1
            }
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

        var result = BufferRebuildUtility.ResumeFromBuffer([], history, "Same prompt", null);

        var promptCount = result.MergedMessages.Count(m => m is { Role: "user", Content: "Same prompt" });
        promptCount.ShouldBe(1);
    }

    [Fact]
    public void ResumeFromBuffer_ExcludesCurrentPromptFromBufferTurns()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "User's question", UserMessage = new UserMessageInfo("Bob", null), SequenceNumber = 1 },
            new() { Content = "Response", MessageId = "msg-1", SequenceNumber = 2 }
        };

        var result = BufferRebuildUtility.ResumeFromBuffer(buffer, [], "User's question", "Bob");

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

        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "Already known content", MessageId = "msg-1", SequenceNumber = 1 }
        };

        var result = BufferRebuildUtility.ResumeFromBuffer(buffer, history, null, null);

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

    [Fact]
    public void ResumeFromBuffer_DoesNotDuplicateNonCurrentPromptUserMessages()
    {
        // Scenario: multi-turn topic reconnects with buffer containing previous user messages.
        // The first user message is in both history (with ID) and buffer (without ID).
        // It should NOT be duplicated in the merged result.
        var history = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "First question", MessageId = "user-1" },
            new() { Role = "assistant", Content = "First answer", MessageId = "asst-1" }
        };

        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "First question", UserMessage = new UserMessageInfo("alice", null), SequenceNumber = 1 },
            new() { Content = "First answer", MessageId = "asst-1", IsComplete = true, SequenceNumber = 2 },
            new() { Content = "Second question", UserMessage = new UserMessageInfo("alice", null), SequenceNumber = 3 },
            new() { Content = "Streaming response", MessageId = "asst-2", SequenceNumber = 4 }
        };

        var result = BufferRebuildUtility.ResumeFromBuffer(
            buffer, history, "Second question", "alice");

        // First question should appear exactly once (from history, not duplicated from buffer)
        var firstQuestionCount = result.MergedMessages
            .Count(m => m is { Role: "user", Content: "First question" });
        firstQuestionCount.ShouldBe(1);

        // Second question (current prompt) should also appear exactly once
        var secondQuestionCount = result.MergedMessages
            .Count(m => m is { Role: "user", Content: "Second question" });
        secondQuestionCount.ShouldBe(1);

        // Total messages: user1, asst1, user2
        result.MergedMessages.Count.ShouldBe(3);
        result.StreamingMessage.Content.ShouldBe("Streaming response");
    }

    [Fact]
    public void ResumeFromBuffer_DoesNotDuplicateUserMessagesOnRepeatedReconnections()
    {
        // Simulate what happens after a first reconnection left duplicates in existingHistory.
        // Even with dirty history, user messages from buffer should not be re-added.
        var dirtyHistory = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Hello", MessageId = "user-1" },
            new() { Role = "user", Content = "Hello" }, // leftover from previous bad merge
            new() { Role = "assistant", Content = "Hi there", MessageId = "asst-1" }
        };

        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "Hello", UserMessage = new UserMessageInfo("alice", null), SequenceNumber = 1 },
            new() { Content = "Hi there", MessageId = "asst-1", IsComplete = true, SequenceNumber = 2 },
            new() { Content = "New question", UserMessage = new UserMessageInfo("alice", null), SequenceNumber = 3 },
            new() { Content = "Response", MessageId = "asst-2", SequenceNumber = 4 }
        };

        var result = BufferRebuildUtility.ResumeFromBuffer(
            buffer, dirtyHistory, "New question", "alice");

        // Buffer user messages should not add more copies
        var helloCount = result.MergedMessages
            .Count(m => m is { Role: "user", Content: "Hello" });
        helloCount.ShouldBeLessThanOrEqualTo(2); // at most the existing dirty count, not worse
    }

    [Fact]
    public void ResumeFromBuffer_CurrentPromptAppearsBeforeUnanchoredBufferContent()
    {
        var history = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Q1", MessageId = "msg-1" },
            new() { Role = "assistant", Content = "A1", MessageId = "msg-2" }
        };

        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "Response to new question", MessageId = "msg-3", IsComplete = true, SequenceNumber = 1 },
            new() { Content = "Still responding", MessageId = "msg-4", SequenceNumber = 2 }
        };

        var result = BufferRebuildUtility.ResumeFromBuffer(buffer, history, "New question", "alice");

        // Order should be: Q1, A1, New question, Response to new question
        result.MergedMessages.Count.ShouldBe(4);
        result.MergedMessages[0].Content.ShouldBe("Q1");
        result.MergedMessages[1].Content.ShouldBe("A1");
        result.MergedMessages[2].Role.ShouldBe("user");
        result.MergedMessages[2].Content.ShouldBe("New question");
        result.MergedMessages[3].Content.ShouldBe("Response to new question");
        result.StreamingMessage.Content.ShouldBe("Still responding");
    }

    #endregion

    #region Same-MessageId IsComplete Split Tests

    [Fact]
    public void RebuildFromBuffer_SameMessageId_ChunksAfterIsComplete_AccumulatesIntoSingleMessage()
    {
        // During streaming, all chunks with the same MessageId are accumulated into ONE message.
        // RebuildFromBuffer should match: IsComplete mid-stream should NOT split the message
        // when more chunks follow with the same MessageId.
        var buffer = new List<ChatStreamMessage>
        {
            new() { Reasoning = "Thinking...", MessageId = "msg-1", SequenceNumber = 1 },
            new() { ToolCalls = "search(query)", MessageId = "msg-1", IsComplete = true, SequenceNumber = 2 },
            new() { Content = "Here is the answer", MessageId = "msg-1", IsComplete = true, SequenceNumber = 3 }
        };

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        // Should be ONE completed message with all fields, not split into two
        completedTurns.Count.ShouldBe(1);
        completedTurns[0].Reasoning.ShouldBe("Thinking...");
        completedTurns[0].ToolCalls.ShouldBe("search(query)");
        completedTurns[0].Content.ShouldBe("Here is the answer");
        completedTurns[0].MessageId.ShouldBe("msg-1");
        streamingMessage.HasContent.ShouldBeFalse();
    }

    [Fact]
    public void RebuildFromBuffer_SameMessageId_ChunksAfterIsComplete_StillStreamingIfLastNotComplete()
    {
        // Same MessageId continues after IsComplete, but last chunk is NOT complete → streaming
        var buffer = new List<ChatStreamMessage>
        {
            new() { ToolCalls = "tool1", MessageId = "msg-1", IsComplete = true, SequenceNumber = 1 },
            new() { Content = "partial answer", MessageId = "msg-1", SequenceNumber = 2 }
        };

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        completedTurns.ShouldBeEmpty();
        streamingMessage.ToolCalls.ShouldBe("tool1");
        streamingMessage.Content.ShouldBe("partial answer");
        streamingMessage.MessageId.ShouldBe("msg-1");
    }

    [Fact]
    public void RebuildFromBuffer_DifferentMessageIds_WithIsComplete_StillSplitsCorrectly()
    {
        // Different MessageIds should still produce separate messages (unchanged behavior)
        var buffer = new List<ChatStreamMessage>
        {
            new() { Reasoning = "R1", ToolCalls = "TC1", MessageId = "msg-1", IsComplete = true, SequenceNumber = 1 },
            new() { Reasoning = "R2", Content = "Answer", MessageId = "msg-2", IsComplete = true, SequenceNumber = 2 }
        };

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        completedTurns.Count.ShouldBe(2);
        completedTurns[0].Reasoning.ShouldBe("R1");
        completedTurns[0].ToolCalls.ShouldBe("TC1");
        completedTurns[0].MessageId.ShouldBe("msg-1");
        completedTurns[1].Reasoning.ShouldBe("R2");
        completedTurns[1].Content.ShouldBe("Answer");
        completedTurns[1].MessageId.ShouldBe("msg-2");
        streamingMessage.HasContent.ShouldBeFalse();
    }

    #endregion
}