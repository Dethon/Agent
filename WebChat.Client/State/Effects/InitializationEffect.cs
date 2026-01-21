using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.State.Effects;

public sealed class InitializationEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IChatConnectionService _connectionService;
    private readonly IAgentService _agentService;
    private readonly ITopicService _topicService;
    private readonly ILocalStorageService _localStorage;
    private readonly ISignalREventSubscriber _eventSubscriber;
    private readonly IStreamResumeService _streamResumeService;
    private readonly UserIdentityStore _userIdentityStore;

    public InitializationEffect(
        Dispatcher dispatcher,
        IChatConnectionService connectionService,
        IAgentService agentService,
        ITopicService topicService,
        ILocalStorageService localStorage,
        ISignalREventSubscriber eventSubscriber,
        IStreamResumeService streamResumeService,
        UserIdentityStore userIdentityStore)
    {
        _dispatcher = dispatcher;
        _connectionService = connectionService;
        _agentService = agentService;
        _topicService = topicService;
        _localStorage = localStorage;
        _eventSubscriber = eventSubscriber;
        _streamResumeService = streamResumeService;
        _userIdentityStore = userIdentityStore;

        dispatcher.RegisterHandler<Initialize>(HandleInitialize);
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

        // Register user after initial connection
        await RegisterUserAsync();

        // Re-register user on reconnection
        _connectionService.OnReconnected += async () => await RegisterUserAsync();

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

    private async Task RegisterUserAsync()
    {
        var userId = _userIdentityStore.State.SelectedUserId;
        if (!string.IsNullOrEmpty(userId) && _connectionService.HubConnection is not null)
        {
            await _connectionService.HubConnection.InvokeAsync("RegisterUser", userId);
        }
    }

    private async Task LoadTopicHistoryAsync(StoredTopic topic)
    {
        var history = await _topicService.GetHistoryAsync(topic.ChatId, topic.ThreadId);
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
        // No subscription to dispose
    }
}