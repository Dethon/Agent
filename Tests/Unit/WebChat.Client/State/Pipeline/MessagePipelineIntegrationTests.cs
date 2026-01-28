using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;

namespace Tests.Unit.WebChat.Client.State.Pipeline;

public sealed class MessagePipelineIntegrationTests
{
    private readonly Dispatcher _dispatcher;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly MessagePipeline _pipeline;

    public MessagePipelineIntegrationTests()
    {
        _dispatcher = new Dispatcher();
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _pipeline = new MessagePipeline(
            _dispatcher,
            _messagesStore,
            _streamingStore,
            NullLogger<MessagePipeline>.Instance);
    }

    [Fact]
    public void FullConversationFlow_UserSendsMessage_AssistantResponds()
    {
        // User sends message
        var correlationId = _pipeline.SubmitUserMessage("topic-1", "Hello", "user-1");

        // Streaming starts
        _dispatcher.Dispatch(new StreamStarted("topic-1"));

        // Chunks arrive (each chunk contains FULL accumulated content, not delta)
        _pipeline.AccumulateChunk("topic-1", "msg-1", "Hi ", null, null);
        _pipeline.AccumulateChunk("topic-1", "msg-1", "Hi there!", null, null);

        // Stream completes
        _pipeline.FinalizeMessage("topic-1", "msg-1");

        // Verify final state
        var messages = _messagesStore.State.MessagesByTopic["topic-1"];
        messages.Count.ShouldBe(2);
        messages[0].Role.ShouldBe("user");
        messages[0].Content.ShouldBe("Hello");
        messages[1].Role.ShouldBe("assistant");
        messages[1].Content.ShouldBe("Hi there!");
    }

    [Fact]
    public void DuplicateFinalization_SkipsSecondAttempt()
    {
        // Start streaming
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _pipeline.AccumulateChunk("topic-1", "msg-1", "Response", null, null);

        // First finalize
        _pipeline.FinalizeMessage("topic-1", "msg-1");

        // Simulate race condition - another source tries to finalize same message
        _pipeline.AccumulateChunk("topic-1", "msg-1", " extra", null, null);
        _pipeline.FinalizeMessage("topic-1", "msg-1");

        // Should only have one message
        var messages = _messagesStore.State.MessagesByTopic["topic-1"];
        messages.Count.ShouldBe(1);
        messages[0].Content.ShouldBe("Response");
    }

    [Fact]
    public void LoadHistory_ThenStream_NoDoubleMessages()
    {
        // Load history with existing message
        var history = new List<ChatHistoryMessage>
        {
            new("msg-1", "assistant", "Previous response", null, null)
        };
        _pipeline.LoadHistory("topic-1", history);

        // Start new stream
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _pipeline.AccumulateChunk("topic-1", "msg-2", "New response", null, null);
        _pipeline.FinalizeMessage("topic-1", "msg-2");

        // Should have both messages
        var messages = _messagesStore.State.MessagesByTopic["topic-1"];
        messages.Count.ShouldBe(2);
    }

    [Fact]
    public void OtherUserMessage_SkippedWhenSentByThisClient()
    {
        // User sends message through pipeline
        var correlationId = _pipeline.SubmitUserMessage("topic-1", "Hello", "user-1");

        // Simulate hub notification with same correlation ID
        var wasSent = _pipeline.WasSentByThisClient(correlationId);

        wasSent.ShouldBeTrue();
    }
}
