using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
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
    private readonly TopicStreams _topicStreams;
    private readonly MessagePipeline _pipeline;
    private readonly TaskCompletionSource _running = new();

    public MessagePipelineIntegrationTests()
    {
        _dispatcher = new Dispatcher();
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _topicStreams = new TopicStreams(_dispatcher, _messagesStore);
        _pipeline = new MessagePipeline(
            _dispatcher,
            _messagesStore,
            _topicStreams,
            NullLogger<MessagePipeline>.Instance);
    }

    [Fact]
    public void FullConversationFlow_UserSendsMessage_AssistantResponds()
    {
        _pipeline.SubmitUserMessage("topic-1", "Hello", "user-1");

        var lease = OpenReply("topic-1");
        lease.Append(new ChatStreamMessage { Content = "Hi ", MessageId = "msg-1" });
        lease.Append(new ChatStreamMessage { Content = "there!", MessageId = "msg-1" });
        lease.Complete();

        var messages = _messagesStore.State.MessagesByTopic["topic-1"];
        messages.Count.ShouldBe(2);
        messages[0].Role.ShouldBe("user");
        messages[0].Content.ShouldBe("Hello");
        messages[1].Role.ShouldBe("assistant");
        messages[1].Content.ShouldBe("Hi there!");
        _streamingStore.State.StreamingByTopic.ShouldNotContainKey("topic-1");
    }

    [Fact]
    public void LoadHistory_ThenStream_NoDoubleMessages()
    {
        var history = new List<ChatHistoryMessage>
        {
            new("msg-1", "assistant", "Previous response", null, null)
        };
        _pipeline.LoadHistory("topic-1", history);

        var lease = OpenReply("topic-1");
        lease.Append(new ChatStreamMessage { Content = "New response", MessageId = "msg-2" });
        lease.Complete();

        var messages = _messagesStore.State.MessagesByTopic["topic-1"];
        messages.Count.ShouldBe(2);
    }

    private StreamLease OpenReply(string topicId) =>
        _topicStreams.TryOpen(
            topicId, new ChatMessageModel { Role = "assistant" }, null, _ => _running.Task)!;
}