using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class TopicSelectionEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly IChatSessionService _sessionService;
    private readonly ITopicService _topicService;
    private readonly IStreamResumeService _streamResumeService;
    private readonly IMessagePipeline _pipeline;

    public TopicSelectionEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        MessagesStore messagesStore,
        IChatSessionService sessionService,
        ITopicService topicService,
        IStreamResumeService streamResumeService,
        IMessagePipeline pipeline)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _messagesStore = messagesStore;
        _sessionService = sessionService;
        _topicService = topicService;
        _streamResumeService = streamResumeService;
        _pipeline = pipeline;

        dispatcher.RegisterHandler<SelectTopic>(HandleSelectTopic);
    }

    private void HandleSelectTopic(SelectTopic action)
    {
        if (action.TopicId is null)
        {
            return;
        }

        _ = HandleSelectTopicAsync(action.TopicId);
    }

    private async Task HandleSelectTopicAsync(string topicId)
    {
        var topic = _topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId);
        if (topic is null)
        {
            return;
        }

        // Check if messages already loaded
        var hasMessages = _messagesStore.State.MessagesByTopic.ContainsKey(topicId);
        if (!hasMessages)
        {
            await _sessionService.StartSessionAsync(topic);
            var history = await _topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);

            // Re-check after async work - SendMessageEffect might have added messages
            var currentMessages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topicId, []);
            if (currentMessages.Count == 0)
            {
                _pipeline.LoadHistory(topicId, history);
            }
        }

        // Mark messages as read by updating LastReadMessageCount
        await MarkTopicAsReadAsync(topic);

        // Try to resume any active streaming
        _ = _streamResumeService.TryResumeStreamAsync(topic);
    }

    private async Task MarkTopicAsReadAsync(StoredTopic topic)
    {
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId, []);
        var assistantCount = messages.Count(m => m.Role != "user");

        if (assistantCount > topic.LastReadMessageCount)
        {
            // Update local state
            var updatedTopic = new StoredTopic
            {
                TopicId = topic.TopicId,
                ChatId = topic.ChatId,
                ThreadId = topic.ThreadId,
                AgentId = topic.AgentId,
                Name = topic.Name,
                CreatedAt = topic.CreatedAt,
                LastMessageAt = topic.LastMessageAt,
                LastReadMessageCount = assistantCount
            };
            _dispatcher.Dispatch(new UpdateTopic(updatedTopic));

            // Persist to server
            await _topicService.SaveTopicAsync(updatedTopic.ToMetadata());
        }
    }

    public void Dispose()
    {
        // No subscription to dispose
    }
}