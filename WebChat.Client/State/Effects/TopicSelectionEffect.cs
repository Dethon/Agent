using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

/// <summary>
/// Handles SelectTopic action: loads history, starts session, resumes streaming.
/// </summary>
public sealed class TopicSelectionEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly IChatSessionService _sessionService;
    private readonly ITopicService _topicService;
    private readonly IStreamResumeService _streamResumeService;

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

    private void HandleSelectTopic(SelectTopic action)
    {
        if (action.TopicId is null) return;

        _ = HandleSelectTopicAsync(action.TopicId);
    }

    private async Task HandleSelectTopicAsync(string topicId)
    {
        var topic = _topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId);
        if (topic is null) return;

        // Check if messages already loaded
        var hasMessages = _messagesStore.State.MessagesByTopic.ContainsKey(topicId);
        if (!hasMessages)
        {
            await _sessionService.StartSessionAsync(topic);
            var history = await _topicService.GetHistoryAsync(topic.ChatId, topic.ThreadId);
            var messages = history.Select(h => new ChatMessageModel
            {
                Role = h.Role,
                Content = h.Content
            }).ToList();
            _dispatcher.Dispatch(new MessagesLoaded(topicId, messages));
        }

        // Try to resume any active streaming
        _ = _streamResumeService.TryResumeStreamAsync(topic);
    }

    public void Dispose()
    {
        // No subscription to dispose
    }
}
