using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class InitializationEffect : IDisposable
{
    private readonly IAgentService _agentService;
    private readonly IChatConnectionService _connectionService;
    private readonly Dispatcher _dispatcher;
    private readonly ISignalREventSubscriber _eventSubscriber;
    private readonly ILocalStorageService _localStorage;
    private readonly IStreamResumeService _streamResumeService;
    private readonly ITopicService _topicService;

    public InitializationEffect(
        Dispatcher dispatcher,
        IChatConnectionService connectionService,
        IAgentService agentService,
        ITopicService topicService,
        ILocalStorageService localStorage,
        ISignalREventSubscriber eventSubscriber,
        IStreamResumeService streamResumeService)
    {
        _dispatcher = dispatcher;
        _connectionService = connectionService;
        _agentService = agentService;
        _topicService = topicService;
        _localStorage = localStorage;
        _eventSubscriber = eventSubscriber;
        _streamResumeService = streamResumeService;

        dispatcher.RegisterHandler<Initialize>(HandleInitialize);
    }

    public void Dispose()
    {
        // No subscription to dispose
    }

    private void HandleInitialize(Initialize action)
    {
        _ = HandleInitializeAsync();
    }

    private async Task HandleInitializeAsync()
    {
        // Connect to SignalR
        await _connectionService.ConnectAsync();
        _eventSubscriber.Subscribe();

        // Load agents
        var agents = await _agentService.GetAgentsAsync();
        _dispatcher.Dispatch(new SetAgents(agents));

        if (agents.Count > 0)
        {
            var savedAgentId = await _localStorage.GetAsync("selectedAgentId");
            var savedAgent = agents.FirstOrDefault(a => a.Id == savedAgentId);
            var agentToSelect = savedAgent ?? agents[0];
            _dispatcher.Dispatch(new SelectAgent(agentToSelect.Id));

            if (savedAgent is null)
            {
                await _localStorage.SetAsync("selectedAgentId", agentToSelect.Id);
            }
        }

        // Load topics
        var serverTopics = await _topicService.GetAllTopicsAsync();
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
        var history = await _topicService.GetHistoryAsync(topic.ChatId, topic.ThreadId);
        var messages = history.Select(h => new ChatMessageModel
        {
            Role = h.Role,
            Content = h.Content
        }).ToList();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, messages));

        _ = _streamResumeService.TryResumeStreamAsync(topic);
    }
}