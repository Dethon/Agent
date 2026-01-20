using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.Services.Utilities;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

/// <summary>
/// Handles SendMessage action coordination: topic creation, session start, streaming.
/// </summary>
public sealed class SendMessageEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly IChatSessionService _sessionService;
    private readonly IStreamingCoordinator _streamingCoordinator;
    private readonly ITopicService _topicService;

    public SendMessageEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        IChatSessionService sessionService,
        IStreamingCoordinator streamingCoordinator,
        ITopicService topicService)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _sessionService = sessionService;
        _streamingCoordinator = streamingCoordinator;
        _topicService = topicService;

        dispatcher.RegisterHandler<SendMessage>(HandleSendMessage);
    }

    private void HandleSendMessage(SendMessage action)
    {
        _ = HandleSendMessageAsync(action);
    }

    private async Task HandleSendMessageAsync(SendMessage action)
    {
        var state = _topicsStore.State;
        StoredTopic topic;

        if (string.IsNullOrEmpty(action.TopicId))
        {
            // Create new topic
            var topicName = action.Message.Length > 50 ? action.Message[..50] + "..." : action.Message;
            var topicId = TopicIdGenerator.GenerateTopicId();
            topic = new StoredTopic
            {
                TopicId = topicId,
                ChatId = TopicIdGenerator.GetChatIdForTopic(topicId),
                ThreadId = TopicIdGenerator.GetThreadIdForTopic(topicId),
                AgentId = state.SelectedAgentId!,
                Name = topicName,
                CreatedAt = DateTime.UtcNow
            };

            var success = await _sessionService.StartSessionAsync(topic);
            if (!success) return;

            _dispatcher.Dispatch(new AddTopic(topic));
            _dispatcher.Dispatch(new SelectTopic(topic.TopicId));
            _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
            await _topicService.SaveTopicAsync(topic.ToMetadata(), isNew: true);
        }
        else
        {
            topic = state.Topics.First(t => t.TopicId == action.TopicId);
            if (_sessionService.CurrentTopic?.TopicId != topic.TopicId)
            {
                await _sessionService.StartSessionAsync(topic);
            }
        }

        // Add user message
        _dispatcher.Dispatch(new AddMessage(topic.TopicId, new ChatMessageModel
        {
            Role = "user",
            Content = action.Message
        }));

        // Start streaming
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        // Kick off streaming (fire-and-forget)
        // Pass no-op render callback since components subscribe to store directly
        _ = _streamingCoordinator.StreamResponseAsync(topic, action.Message, () => Task.CompletedTask);
    }

    public void Dispose()
    {
        // No subscription to dispose, handler is registered with dispatcher
    }
}
