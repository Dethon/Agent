using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Hub;

public sealed class ReconnectionEffect : IDisposable
{
    private readonly IDisposable _subscription;
    private readonly Dispatcher _dispatcher;
    private readonly ITopicService _topicService;
    private int? _lastHandledEpoch;

    public ReconnectionEffect(
        ConnectionStore connectionStore,
        TopicsStore topicsStore,
        SpaceStore spaceStore,
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
                if (state.Status != ConnectionStatus.Connected)
                {
                    return;
                }

                // The first epoch seen is the connection this client started on, so there is
                // nothing to catch up on. Every later one is an interruption, whether or not
                // a disconnected status was ever observed in between.
                var isCatchUp = _lastHandledEpoch is { } lastEpoch && state.Epoch > lastEpoch;
                _lastHandledEpoch = state.Epoch;

                if (isCatchUp)
                {
                    _ = HandleReconnectedAsync(topicsStore, spaceStore, sessionService, streamResumeService);
                }
            });
    }

    private async Task HandleReconnectedAsync(
        TopicsStore topicsStore,
        SpaceStore spaceStore,
        IChatSessionService sessionService,
        IStreamResumeService streamResumeService)
    {
        // Re-fetch topics from server to pick up changes made while disconnected
        var agentId = topicsStore.State.SelectedAgentId;
        if (agentId is not null)
        {
            var spaceSlug = spaceStore.State.CurrentSlug;
            var serverTopics = await _topicService.GetAllTopicsAsync(agentId, spaceSlug);
            var topics = serverTopics.Select(StoredTopic.FromMetadata).ToList();
            _dispatcher.Dispatch(new TopicsLoaded(topics));
        }

        var currentState = topicsStore.State;

        var tasks = new List<Task>();

        if (currentState.SelectedTopicId is not null)
        {
            var selectedTopic = currentState.Topics
                .FirstOrDefault(t => t.TopicId == currentState.SelectedTopicId);

            if (selectedTopic is not null)
            {
                tasks.Add(ReloadTopicHistoryAsync(selectedTopic));
                tasks.Add(sessionService.StartSessionAsync(selectedTopic));
            }
        }

        tasks.AddRange(currentState.Topics.Select(streamResumeService.TryResumeStreamAsync));

        await Task.WhenAll(tasks);
    }

    private async Task ReloadTopicHistoryAsync(StoredTopic topic)
    {
        var history = await _topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);
        var messages = history.Select(h => h.ToChatMessageModel()).ToList();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, messages));
    }

    public void Dispose()
    {
        _subscription.Dispose();
    }
}