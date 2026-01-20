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
        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer([], []);

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

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer, []);

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

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer, []);

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

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer, []);

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

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer, []);

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

        var (_, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer, []);

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

        var (_, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer, []);

        streamingMessage.ToolCalls.ShouldBe("tool_1\ntool_2");
    }

    [Fact]
    public void RebuildFromBuffer_StripsKnownContent_FromAllTurns()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "Known content here", MessageId = "msg-1" }
        };
        var historyContent = new HashSet<string> { "Known content here" };

        var (_, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer, historyContent);

        streamingMessage.Content.ShouldBeEmpty();
    }

    [Fact]
    public void RebuildFromBuffer_SkipsEmptyCompletedTurns()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { IsComplete = true, MessageId = "msg-1" },
            new() { Content = "Second turn", MessageId = "msg-2" }
        };

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer, []);

        completedTurns.ShouldBeEmpty();
        streamingMessage.Content.ShouldBe("Second turn");
    }

    #endregion

    #region StripKnownContent Tests

    [Fact]
    public void StripKnownContent_WhenBufferIsSubsetOfHistory_ReturnsEmpty()
    {
        var message = new ChatMessageModel { Role = "assistant", Content = "partial" };
        var historyContent = new HashSet<string> { "partial content is longer" };

        var result = BufferRebuildUtility.StripKnownContent(message, historyContent);

        result.Content.ShouldBeEmpty();
    }

    [Fact]
    public void StripKnownContent_WhenBufferHasMoreThanHistory_StripsPrefix()
    {
        var message = new ChatMessageModel { Role = "assistant", Content = "Known new content" };
        var historyContent = new HashSet<string> { "Known" };

        var result = BufferRebuildUtility.StripKnownContent(message, historyContent);

        result.Content.ShouldBe("new content");
    }

    [Fact]
    public void StripKnownContent_WhenNoOverlap_ReturnsUnchanged()
    {
        var message = new ChatMessageModel { Role = "assistant", Content = "completely new" };
        var historyContent = new HashSet<string> { "something else" };

        var result = BufferRebuildUtility.StripKnownContent(message, historyContent);

        result.Content.ShouldBe("completely new");
    }

    [Fact]
    public void StripKnownContent_WithEmptyContent_ReturnsUnchanged()
    {
        var message = new ChatMessageModel { Role = "assistant", Content = "" };
        var historyContent = new HashSet<string> { "something" };

        var result = BufferRebuildUtility.StripKnownContent(message, historyContent);

        result.Content.ShouldBeEmpty();
    }

    [Fact]
    public void StripKnownContent_PreservesOtherFields()
    {
        var message = new ChatMessageModel
        {
            Role = "assistant",
            Content = "Known new",
            Reasoning = "thinking",
            ToolCalls = "tool_1"
        };
        var historyContent = new HashSet<string> { "Known" };

        var result = BufferRebuildUtility.StripKnownContent(message, historyContent);

        result.Reasoning.ShouldBe("thinking");
        result.ToolCalls.ShouldBe("tool_1");
    }

    #endregion
}
