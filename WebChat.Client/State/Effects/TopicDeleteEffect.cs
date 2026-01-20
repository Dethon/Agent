using WebChat.Client.Contracts;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

/// <summary>
/// Handles RemoveTopic action: cancels streaming, deletes from server, clears approval.
/// </summary>
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
        _ = HandleRemoveTopicAsync(action.TopicId);
    }

    private async Task HandleRemoveTopicAsync(string topicId)
    {
        var topic = _topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId);
        if (topic is null) return;

        // Cancel any active streaming
        if (_streamingStore.State.StreamingByTopic.ContainsKey(topicId))
        {
            await _messagingService.CancelTopicAsync(topicId);
            _dispatcher.Dispatch(new StreamCancelled(topicId));
        }

        // Delete from server
        await _topicService.DeleteTopicAsync(topicId, topic.ChatId, topic.ThreadId);

        // Clear approval if this was the selected topic
        if (_topicsStore.State.SelectedTopicId == topicId)
        {
            _dispatcher.Dispatch(new ClearApproval());
        }

        // Note: The reducer handles removing from state when RemoveTopic is dispatched
        // The effect handles the async side effects (cancel, delete from server)
    }

    public void Dispose()
    {
        // No subscription to dispose
    }
}
