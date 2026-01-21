using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class TopicSelectionEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly MessagesStore _messagesStore;
    private readonly IChatSessionService _sessionService;
    private readonly IStreamResumeService _streamResumeService;
    private readonly ITopicService _topicService;
    private readonly TopicsStore _topicsStore;

    public TopicSelectionEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        MessagesStore messagesStore,
        IChatSessionService sessionService,
        ITopicService topicService,
        IStreamResumeService streamResumeService)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _messagesStore = messagesStore;
        _sessionService = sessionService;
        _topicService = topicService;
        _streamResumeService = streamResumeService;

        dispatcher.RegisterHandler<SelectTopic>(HandleSelectTopic);
    }

    public void Dispose()
    {
        // No subscription to dispose
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
            var history = await _topicService.GetHistoryAsync(topic.ChatId, topic.ThreadId);

            // Re-check after async work - SendMessageEffect might have added messages
            var currentMessages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topicId, []);
            if (currentMessages.Count == 0)
            {
                var messages = history.Select(h => new ChatMessageModel
                {
                    Role = h.Role,
                    Content = h.Content
                }).ToList();
                _dispatcher.Dispatch(new MessagesLoaded(topicId, messages));
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
}