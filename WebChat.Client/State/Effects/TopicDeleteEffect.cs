using WebChat.Client.Contracts;
using WebChat.Client.State.Approval;
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

    public TopicDeleteEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        StreamingStore streamingStore,
        IChatMessagingService messagingService,
        ITopicService topicService)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _streamingStore = streamingStore;
        _messagingService = messagingService;
        _topicService = topicService;

        dispatcher.RegisterHandler<RemoveTopic>(HandleRemoveTopic);
    }

    private void HandleRemoveTopic(RemoveTopic action)
    {
        _ = HandleRemoveTopicAsync(action);
    }

    private async Task HandleRemoveTopicAsync(RemoveTopic action)
    {
        // Cancel any active streaming
        if (_streamingStore.State.StreamingByTopic.ContainsKey(action.TopicId))
        {
            await _messagingService.CancelTopicAsync(action.TopicId);
            _dispatcher.Dispatch(new StreamCancelled(action.TopicId));
        }

        // Delete from server only if ChatId/ThreadId provided (client-initiated delete)
        // When server sends delete notification, these are null (already deleted server-side)
        if (action.ChatId.HasValue && action.ThreadId.HasValue)
        {
            await _topicService.DeleteTopicAsync(action.TopicId, action.ChatId.Value, action.ThreadId.Value);
        }

        // Clear approval if this was the selected topic
        if (_topicsStore.State.SelectedTopicId == action.TopicId)
        {
            _dispatcher.Dispatch(new ClearApproval());
        }
    }

    public void Dispose()
    {
        // No subscription to dispose
    }
}