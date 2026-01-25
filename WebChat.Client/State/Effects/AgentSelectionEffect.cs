using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class AgentSelectionEffect : IDisposable
{
    private readonly IDisposable _subscription;
    private readonly Dispatcher _dispatcher;
    private readonly IChatSessionService _sessionService;
    private readonly ILocalStorageService _localStorage;
    private readonly ITopicService _topicService;
    private readonly IStreamResumeService _streamResumeService;
    private string? _previousAgentId;

    public AgentSelectionEffect(
        TopicsStore topicsStore,
        Dispatcher dispatcher,
        IChatSessionService sessionService,
        ILocalStorageService localStorage,
        ITopicService topicService,
        IStreamResumeService streamResumeService)
    {
        _dispatcher = dispatcher;
        _sessionService = sessionService;
        _localStorage = localStorage;
        _topicService = topicService;
        _streamResumeService = streamResumeService;

        // Subscribe to store to detect agent changes
        _subscription = topicsStore.StateObservable.Subscribe(HandleStateChange);
    }

    private void HandleStateChange(TopicsState state)
    {
        if (state.SelectedAgentId != _previousAgentId && _previousAgentId is not null)
        {
            // Agent changed - clear session, save selection, and reload topics
            _sessionService.ClearSession();
            _ = _localStorage.SetAsync("selectedAgentId", state.SelectedAgentId ?? "");
            _ = LoadTopicsForAgentAsync(state.SelectedAgentId);
        }

        _previousAgentId = state.SelectedAgentId;
    }

    private async Task LoadTopicsForAgentAsync(string? agentId)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            _dispatcher.Dispatch(new TopicsLoaded([]));
            return;
        }

        var serverTopics = await _topicService.GetAllTopicsAsync(agentId);
        var topics = serverTopics.Select(StoredTopic.FromMetadata).ToList();
        _dispatcher.Dispatch(new TopicsLoaded(topics));

        // Load history for each topic (fire-and-forget)
        foreach (var topic in topics)
        {
            _ = LoadTopicHistoryAsync(topic);
        }
    }

    private async Task LoadTopicHistoryAsync(StoredTopic topic)
    {
        var history = await _topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);
        var messages = history.Select(h => new ChatMessageModel
        {
            Role = h.Role,
            Content = h.Content,
            SenderId = h.SenderId
        }).ToList();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, messages));

        _ = _streamResumeService.TryResumeStreamAsync(topic);
    }

    public void Dispose()
    {
        _subscription.Dispose();
    }
}