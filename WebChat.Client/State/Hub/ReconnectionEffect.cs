using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Hub;

public sealed class ReconnectionEffect : IDisposable
{
    private readonly IDisposable _subscription;
    private readonly Dispatcher _dispatcher;
    private readonly ITopicService _topicService;
    private ConnectionStatus _previousStatus = ConnectionStatus.Disconnected;
    private bool _wasEverConnected;

    public ReconnectionEffect(
        ConnectionStore connectionStore,
        TopicsStore topicsStore,
        IChatSessionService sessionService,
        IStreamResumeService streamResumeService,
        Dispatcher dispatcher,
        ITopicService topicService)
    {
        _dispatcher = dispatcher;
        _topicService = topicService;

        _subscription = connectionStore.StateObservable
            .Subscribe(state =>
            {
                var wasDisconnected = _previousStatus is ConnectionStatus.Reconnecting or ConnectionStatus.Disconnected;
                var isNowConnected = state.Status == ConnectionStatus.Connected;

                // Track first connection to avoid reload on initial page load
                if (isNowConnected && !_wasEverConnected)
                {
                    _wasEverConnected = true;
                    _previousStatus = state.Status;
                    return;
                }

                _previousStatus = state.Status;

                // Reload history when reconnecting from any disconnected state
                if (_wasEverConnected && wasDisconnected && isNowConnected)
                {
                    _ = HandleReconnectedAsync(topicsStore, sessionService, streamResumeService);
                }
            });
    }

    private async Task HandleReconnectedAsync(
        TopicsStore topicsStore,
        IChatSessionService sessionService,
        IStreamResumeService streamResumeService)
    {
        var currentState = topicsStore.State;

        // Reload history and restart session for selected topic
        if (currentState.SelectedTopicId is not null)
        {
            var selectedTopic = currentState.Topics
                .FirstOrDefault(t => t.TopicId == currentState.SelectedTopicId);

            if (selectedTopic is not null)
            {
                await ReloadTopicHistoryAsync(selectedTopic);
                _ = sessionService.StartSessionAsync(selectedTopic);
            }
        }

        // Resume streams for all topics (fire-and-forget)
        foreach (var topic in currentState.Topics)
        {
            _ = streamResumeService.TryResumeStreamAsync(topic);
        }
    }

    private async Task ReloadTopicHistoryAsync(StoredTopic topic)
    {
        var history = await _topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);
        var messages = history.Select(h => new ChatMessageModel
        {
            Role = h.Role,
            Content = h.Content,
            SenderId = h.SenderId,
            Timestamp = h.Timestamp
        }).ToList();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, messages));
    }

    public void Dispose()
    {
        _subscription.Dispose();
    }
}