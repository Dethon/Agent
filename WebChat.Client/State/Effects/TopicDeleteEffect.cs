using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class TopicDeleteEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly StreamingStore _streamingStore;
    private readonly IChatMessagingService _messagingService;
    private readonly ITopicService _topicService;
    private readonly IMessagePipeline _pipeline;
    private readonly ILogger<TopicDeleteEffect> _logger;

    public TopicDeleteEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        StreamingStore streamingStore,
        IChatMessagingService messagingService,
        ITopicService topicService,
        IMessagePipeline pipeline,
        ILogger<TopicDeleteEffect> logger)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _streamingStore = streamingStore;
        _messagingService = messagingService;
        _topicService = topicService;
        _pipeline = pipeline;
        _logger = logger;

        dispatcher.RegisterHandler<RemoveTopic>(action =>
            HandleRemoveTopicAsync(action.TopicId, action.AgentId, action.ChatId, action.ThreadId)
                .LogFaults(_logger, nameof(RemoveTopic)));
    }

    public async Task HandleRemoveTopicAsync(
        string topicId, string? agentId = null, long? chatId = null, long? threadId = null)
    {
        if (_streamingStore.State.StreamingByTopic.ContainsKey(topicId))
        {
            await _messagingService.CancelTopicAsync(topicId);
            _dispatcher.Dispatch(new StreamCancelled(topicId));
        }

        // Delete from server only if AgentId/ChatId/ThreadId provided (client-initiated delete)
        // When server sends delete notification, these are null (already deleted server-side)
        if (agentId is not null && chatId.HasValue && threadId.HasValue)
        {
            await _topicService.DeleteTopicAsync(agentId, topicId, chatId.Value, threadId.Value);
        }

        // Clear cached messages so re-created topics reload from server; the same action drops
        // the topic's finalized message ids, which is all the pipeline ever tracked.
        _dispatcher.Dispatch(new ClearMessages(topicId));

        if (_topicsStore.State.SelectedTopicId == topicId)
        {
            _dispatcher.Dispatch(new ClearApproval());
        }
    }

    public void Dispose()
    {
        // No subscription to dispose
    }
}