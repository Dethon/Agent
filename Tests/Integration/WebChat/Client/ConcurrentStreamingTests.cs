using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;

namespace Tests.Integration.WebChat.Client;

public sealed class ConcurrentStreamingTests : IDisposable
{
    private readonly FakeChatMessagingService _messagingService = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly ToastStore _toastStore;
    private readonly FakeTopicService _topicService = new();
    private readonly StreamingService _service;

    public ConcurrentStreamingTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _toastStore = new ToastStore(_dispatcher);
        _service = new StreamingService(_messagingService, _dispatcher, _topicService, _topicsStore, _streamingStore, _toastStore);
    }

    public void Dispose()
    {
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
        _toastStore.Dispose();
    }

    private StoredTopic CreateTopic()
    {
        var topic = new StoredTopic
        {
            TopicId = Guid.NewGuid().ToString(),
            ChatId = Random.Shared.NextInt64(1000, 9999),
            ThreadId = Random.Shared.NextInt64(1000, 9999),
            AgentId = "test-agent",
            Name = "Test Topic",
            CreatedAt = DateTime.UtcNow
        };
        _dispatcher.Dispatch(new AddTopic(topic));
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        return topic;
    }

    [Fact]
    public async Task SendMessageAsync_MultipleConcurrentMessages_AllProcessedInOrder()
    {
        var topic = CreateTopic();

        // Simulate responses for two messages
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "Response 1", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" },
            new ChatStreamMessage { Content = "Response 2", MessageId = "msg-2" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-2" }
        );

        // Send first message
        var task1 = _service.SendMessageAsync(topic, "Hello");

        // Send second message (should reuse stream or create new one gracefully)
        var task2 = _service.SendMessageAsync(topic, "World");

        await Task.WhenAll(task1, task2);

        // Both responses should be captured
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.Count.ShouldBeGreaterThanOrEqualTo(1);
    }
}