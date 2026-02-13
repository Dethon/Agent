using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.Services;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.State.Effects;

public sealed class InitializationEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IChatConnectionService _connectionService;
    private readonly IAgentService _agentService;
    private readonly ITopicService _topicService;
    private readonly ConfigService _configService;
    private readonly ILocalStorageService _localStorage;
    private readonly ISignalREventSubscriber _eventSubscriber;
    private readonly IStreamResumeService _streamResumeService;
    private readonly PushNotificationService _pushNotificationService;
    private readonly UserIdentityStore _userIdentityStore;
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly IMessagePipeline _pipeline;
    private readonly SpaceStore _spaceStore;

    public InitializationEffect(
        Dispatcher dispatcher,
        IChatConnectionService connectionService,
        IAgentService agentService,
        ITopicService topicService,
        ConfigService configService,
        ILocalStorageService localStorage,
        ISignalREventSubscriber eventSubscriber,
        IStreamResumeService streamResumeService,
        PushNotificationService pushNotificationService,
        UserIdentityStore userIdentityStore,
        TopicsStore topicsStore,
        MessagesStore messagesStore,
        IMessagePipeline pipeline,
        SpaceStore spaceStore)
    {
        _dispatcher = dispatcher;
        _connectionService = connectionService;
        _agentService = agentService;
        _topicService = topicService;
        _configService = configService;
        _localStorage = localStorage;
        _eventSubscriber = eventSubscriber;
        _streamResumeService = streamResumeService;
        _pushNotificationService = pushNotificationService;
        _userIdentityStore = userIdentityStore;
        _topicsStore = topicsStore;
        _messagesStore = messagesStore;
        _pipeline = pipeline;
        _spaceStore = spaceStore;

        dispatcher.RegisterHandler<Initialize>(HandleInitialize);
        dispatcher.RegisterHandler<SelectUser>(HandleSelectUser);
    }

    private void HandleSelectUser(SelectUser action)
    {
        _ = RegisterAndSubscribeAsync(action.UserId);
    }

    private async Task RegisterAndSubscribeAsync(string userId)
    {
        await RegisterUserAsync(userId);
        await SubscribePushAsync();
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
        await SubscribePushAsync();

        // Re-register user on reconnection
        _connectionService.OnReconnected += async () =>
        {
            await RegisterUserAsync();
            await SubscribePushAsync();
            await _topicService.JoinSpaceAsync(_spaceStore.State.CurrentSlug);
        };

        // Validate and join space
        var spaceSlug = _spaceStore.State.CurrentSlug;
        var space = await _configService.GetSpaceAsync(spaceSlug);
        if (space is null)
        {
            _dispatcher.Dispatch(new InvalidSpace());
            spaceSlug = _spaceStore.State.CurrentSlug;
            space = await _configService.GetSpaceAsync(spaceSlug);
        }

        if (space is not null)
        {
            await _topicService.JoinSpaceAsync(spaceSlug);
            _dispatcher.Dispatch(new SpaceValidated(spaceSlug, space.Name, space.AccentColor));
        }

        // Load agents
        var agents = await _agentService.GetAgentsAsync();
        _dispatcher.Dispatch(new SetAgents(agents));

        if (agents.Count == 0)
        {
            return;
        }

        var savedAgentId = await _localStorage.GetAsync("selectedAgentId");
        var savedAgent = agents.FirstOrDefault(a => a.Id == savedAgentId);
        var agentToSelect = savedAgent ?? agents[0];
        _dispatcher.Dispatch(new SelectAgent(agentToSelect.Id));

        if (savedAgent is null)
        {
            await _localStorage.SetAsync("selectedAgentId", agentToSelect.Id);
        }

        // Load topics for selected agent
        var serverTopics = await _topicService.GetAllTopicsAsync(agentToSelect.Id, spaceSlug);
        var topics = serverTopics.Select(StoredTopic.FromMetadata).ToList();
        _dispatcher.Dispatch(new TopicsLoaded(topics));

        // Load history for each topic (fire-and-forget)
        foreach (var topic in topics)
        {
            _ = LoadTopicHistoryAsync(topic);
        }
    }

    private async Task RegisterUserAsync(string? userId = null)
    {
        userId ??= _userIdentityStore.State.SelectedUserId;
        if (!string.IsNullOrEmpty(userId) && _connectionService.HubConnection is not null)
        {
            await _connectionService.HubConnection.InvokeAsync("RegisterUser", userId);
        }
    }

    private async Task SubscribePushAsync()
    {
        try
        {
            var config = await _configService.GetConfigAsync();
            if (!string.IsNullOrEmpty(config.VapidPublicKey))
            {
                await _pushNotificationService.RequestAndSubscribeAsync(config.VapidPublicKey);
            }
        }
        catch
        {
            // Push subscription is best-effort — don't block the app
        }
    }

    private async Task LoadTopicHistoryAsync(StoredTopic topic)
    {
        var history = await _topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);
        _pipeline.LoadHistory(topic.TopicId, history);

        // If this topic is currently selected, mark it as read so no stale badges appear
        if (_topicsStore.State.SelectedTopicId == topic.TopicId)
        {
            await MarkTopicAsReadAsync(topic);
        }

        _ = _streamResumeService.TryResumeStreamAsync(topic);
    }

    private async Task MarkTopicAsReadAsync(StoredTopic topic)
    {
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId, []);
        var lastMessageId = messages.LastOrDefault(m => m.MessageId is not null)?.MessageId;

        if (lastMessageId is not null && lastMessageId != topic.LastReadMessageId)
        {
            var updatedTopic = new StoredTopic
            {
                TopicId = topic.TopicId,
                ChatId = topic.ChatId,
                ThreadId = topic.ThreadId,
                AgentId = topic.AgentId,
                Name = topic.Name,
                CreatedAt = topic.CreatedAt,
                LastMessageAt = topic.LastMessageAt,
                LastReadMessageId = lastMessageId,
                SpaceSlug = topic.SpaceSlug
            };
            _dispatcher.Dispatch(new UpdateTopic(updatedTopic));
            await _topicService.SaveTopicAsync(updatedTopic.ToMetadata());
        }
    }

    public void Dispose()
    {
        // No subscription to dispose
    }
}