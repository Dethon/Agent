using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;

namespace Tests.Unit.WebChat.Client.State.Pipeline;

public sealed class MessagePipelineTests
{
    private readonly Dispatcher _dispatcher;
    private readonly MessagesStore _messagesStore;
    private readonly MessagePipeline _pipeline;

    public MessagePipelineTests()
    {
        _dispatcher = new Dispatcher();
        _messagesStore = new MessagesStore(_dispatcher);
        var streamingStore = new StreamingStore(_dispatcher);
        _pipeline = new MessagePipeline(
            _dispatcher,
            _messagesStore,
            streamingStore,
            NullLogger<MessagePipeline>.Instance);
    }

    [Fact]
    public void SubmitUserMessage_ReturnsCorrelationId()
    {
        var id = _pipeline.SubmitUserMessage("topic-1", "Hello", "user-1");

        id.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void SubmitUserMessage_DispatchesAddMessage()
    {
        _pipeline.SubmitUserMessage("topic-1", "Hello", "user-1");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault("topic-1");
        messages.ShouldNotBeNull();
        messages.Count.ShouldBe(1);
        messages[0].Role.ShouldBe("user");
        messages[0].Content.ShouldBe("Hello");
        messages[0].SenderId.ShouldBe("user-1");
    }

    [Fact]
    public void SubmitUserMessage_TracksAsPending()
    {
        _pipeline.SubmitUserMessage("topic-1", "Hello", "user-1");

        var snapshot = _pipeline.GetSnapshot("topic-1");
        snapshot.PendingUserMessages.ShouldBe(1);
    }

    [Fact]
    public void AccumulateChunk_SkipsDuplicateAfterFinalize()
    {
        _pipeline.AccumulateChunk("topic-1", "msg-1", "Hello", null, null);
        _pipeline.FinalizeMessage("topic-1", "msg-1");

        // This should be skipped
        _pipeline.AccumulateChunk("topic-1", "msg-1", " duplicate", null, null);

        var snapshot = _pipeline.GetSnapshot("topic-1");
        snapshot.FinalizedCount.ShouldBe(1);
    }

    [Fact]
    public void FinalizeMessage_SkipsSecondFinalize()
    {
        // Start streaming
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _pipeline.AccumulateChunk("topic-1", "msg-1", "Content", null, null);

        // First finalize
        _pipeline.FinalizeMessage("topic-1", "msg-1");
        var countAfterFirst = _messagesStore.State.MessagesByTopic
            .GetValueOrDefault("topic-1")?.Count ?? 0;

        // Second finalize should skip
        _pipeline.FinalizeMessage("topic-1", "msg-1");
        var countAfterSecond = _messagesStore.State.MessagesByTopic
            .GetValueOrDefault("topic-1")?.Count ?? 0;

        countAfterFirst.ShouldBe(1);
        countAfterSecond.ShouldBe(1);
    }

    [Fact]
    public void WasSentByThisClient_ReturnsTrueForTrackedCorrelationId()
    {
        var correlationId = _pipeline.SubmitUserMessage("topic-1", "Hello", "user-1");

        _pipeline.WasSentByThisClient(correlationId).ShouldBeTrue();
    }

    [Fact]
    public void WasSentByThisClient_ReturnsFalseForUnknownCorrelationId()
    {
        _pipeline.WasSentByThisClient("unknown-id").ShouldBeFalse();
    }

    [Fact]
    public void WasSentByThisClient_ReturnsFalseForNull()
    {
        _pipeline.WasSentByThisClient(null).ShouldBeFalse();
    }

    [Fact]
    public void LoadHistory_TracksFinalizedMessageIds()
    {
        var history = new List<ChatHistoryMessage>
        {
            new("msg-1", "assistant", "Hello", null, null),
            new("msg-2", "assistant", "World", null, null)
        };

        _pipeline.LoadHistory("topic-1", history);

        var snapshot = _pipeline.GetSnapshot("topic-1");
        snapshot.FinalizedCount.ShouldBe(2);
    }

    [Fact]
    public void LoadHistory_DispatchesMessagesLoaded()
    {
        var history = new List<ChatHistoryMessage>
        {
            new("msg-1", "user", "Hello", null, null),
            new("msg-2", "assistant", "Hi there", null, null)
        };

        _pipeline.LoadHistory("topic-1", history);

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault("topic-1");
        messages.ShouldNotBeNull();
        messages.Count.ShouldBe(2);
    }

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
}