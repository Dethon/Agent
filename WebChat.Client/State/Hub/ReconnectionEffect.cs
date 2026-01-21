using WebChat.Client.Contracts;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Hub;

public sealed class ReconnectionEffect : IDisposable
{
    private readonly IDisposable _subscription;
    private ConnectionStatus _previousStatus = ConnectionStatus.Disconnected;

    public ReconnectionEffect(
        ConnectionStore connectionStore,
        TopicsStore topicsStore,
        IChatSessionService sessionService,
        IStreamResumeService streamResumeService)
    {
        _subscription = connectionStore.StateObservable
            .Subscribe(state =>
            {
                var wasReconnecting = _previousStatus == ConnectionStatus.Reconnecting;
                var isNowConnected = state.Status == ConnectionStatus.Connected;
                _previousStatus = state.Status;

                if (wasReconnecting && isNowConnected)
                {
                    HandleReconnected(topicsStore, sessionService, streamResumeService);
                }
            });
    }

    public void Dispose()
    {
        _subscription.Dispose();
    }

    private static void HandleReconnected(
        TopicsStore topicsStore,
        IChatSessionService sessionService,
        IStreamResumeService streamResumeService)
    {
        var currentState = topicsStore.State;

        // Restart session for selected topic
        if (currentState.SelectedTopicId is not null)
        {
            var selectedTopic = currentState.Topics
                .FirstOrDefault(t => t.TopicId == currentState.SelectedTopicId);

            if (selectedTopic is not null)
            {
                _ = sessionService.StartSessionAsync(selectedTopic);
            }
        }

        // Resume streams for all topics (fire-and-forget)
        foreach (var topic in currentState.Topics)
        {
            _ = streamResumeService.TryResumeStreamAsync(topic);
        }
    }
}