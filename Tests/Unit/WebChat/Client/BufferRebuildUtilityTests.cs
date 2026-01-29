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

    #endregion
}