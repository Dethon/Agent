using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class TopicDeleteEffect
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
        // AgentId/ChatId/ThreadId present means the user asked for this delete. A server
        // notification carries none of them, because the server already deleted the topic.
        var userInitiated = agentId is not null && chatId.HasValue && threadId.HasValue;

        if (_streamingStore.State.StreamingByTopic.ContainsKey(topicId))
        {
            var cancelled = await _messagingService.CancelTopicAsync(topicId);

            // Only a user-initiated delete stops on a cancel that could not be made. For a
            // server-initiated removal the cancel is best-effort cleanup of a stream the server
            // already ended: toasting would blame the user for an action they never took, and
            // stopping would leave a ghost row for a topic that no longer exists.
            if (!cancelled.IsLive && userInitiated)
            {
                _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
                return;
            }

            _dispatcher.Dispatch(new StreamCancelled(topicId));
        }

        // Delete from server only if AgentId/ChatId/ThreadId provided (client-initiated delete)
        // When server sends delete notification, these are null (already deleted server-side)
        if (agentId is not null && chatId.HasValue && threadId.HasValue)
        {
            var deleted = await _topicService.DeleteTopicAsync(agentId, topicId, chatId.Value, threadId.Value);
            if (!deleted.IsLive)
            {
                _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
                return;
            }
        }

        // The user may have switched conversations while the round trip above was in flight,
        // so whether the pending approval belongs to this one is read here, at dispatch time —
        // and before TopicRemoved, whose reduce clears the selection.
        var wasSelected = _topicsStore.State.SelectedTopicId == topicId;

        _dispatcher.Dispatch(new TopicRemoved(topicId));

        // Clear cached messages so re-created topics reload from server; the same action drops
        // the topic's finalized message ids, which is all the pipeline ever tracked.
        _dispatcher.Dispatch(new ClearMessages(topicId));

        if (wasSelected)
        {
            _dispatcher.Dispatch(new ClearApproval());
        }
    }
}