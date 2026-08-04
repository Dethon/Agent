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
    private readonly ILogger<TopicSelectionEffect> _logger;

    public TopicSelectionEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        MessagesStore messagesStore,
        IChatSessionService sessionService,
        ITopicService topicService,
        IStreamResumeService streamResumeService,
        IMessagePipeline pipeline,
        ILogger<TopicSelectionEffect> logger)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _messagesStore = messagesStore;
        _sessionService = sessionService;
        _topicService = topicService;
        _streamResumeService = streamResumeService;
        _pipeline = pipeline;
        _logger = logger;

        dispatcher.RegisterHandler<SelectTopic>(action =>
        {
            if (action.TopicId is not null)
            {
                HandleSelectTopicAsync(action.TopicId).LogFaults(_logger, nameof(SelectTopic));
            }
        });
    }

    public async Task HandleSelectTopicAsync(string topicId)
    {
        var topic = _topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId);
        if (topic is null)
        {
            return;
        }

        var hasMessages = _messagesStore.State.MessagesByTopic.ContainsKey(topicId);
        if (!hasMessages)
        {
            await _sessionService.StartSessionAsync(topic);
            var history = await _topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);

            // Re-check after async work - SendMessageEffect might have added messages
            var currentMessages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topicId, []);
            if (history.IsLive && currentMessages.Count == 0)
            {
                _pipeline.LoadHistory(topicId, history.Value!);
            }
        }

        await MarkTopicAsReadAsync(topic);

        // Detached on purpose: a resumed stream is long-lived, so awaiting it would mean
        // awaiting the conversation.
        _streamResumeService.TryResumeStreamAsync(topic).LogFaults(_logger, "stream resume");
    }

    private async Task MarkTopicAsReadAsync(StoredTopic topic)
    {
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId, []);
        var lastMessageId = messages.LastOrDefault(m => m.MessageId is not null)?.MessageId;

        if (lastMessageId is not null && lastMessageId != topic.LastReadMessageId)
        {
            var updatedTopic = new StoredTopic
            {
                TopicId = topic.TopicId,
                ChatId = topic.ChatId,
                ThreadId = topic.ThreadId,
                AgentId = topic.AgentId,
                Name = topic.Name,
                CreatedAt = topic.CreatedAt,
                LastMessageAt = topic.LastMessageAt,
                LastReadMessageId = lastMessageId,
                SpaceSlug = topic.SpaceSlug
            };
            _dispatcher.Dispatch(new UpdateTopic(updatedTopic));

            await _topicService.SaveTopicAsync(updatedTopic.ToMetadata());
        }
    }

    public void Dispose()
    {
        // No subscription to dispose
    }
}