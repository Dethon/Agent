using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
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
    private readonly IConfigService _configService;
    private readonly ILocalStorageService _localStorage;
    private readonly ISignalREventSubscriber _eventSubscriber;
    private readonly IStreamResumeService _streamResumeService;
    private readonly IPushSubscriptionService _pushNotificationService;
    private readonly UserIdentityStore _userIdentityStore;
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly IMessagePipeline _pipeline;
    private readonly SpaceStore _spaceStore;
    private readonly ILogger<InitializationEffect> _logger;

    public InitializationEffect(
        Dispatcher dispatcher,
        IChatConnectionService connectionService,
        IAgentService agentService,
        ITopicService topicService,
        IConfigService configService,
        ILocalStorageService localStorage,
        ISignalREventSubscriber eventSubscriber,
        IStreamResumeService streamResumeService,
        IPushSubscriptionService pushNotificationService,
        UserIdentityStore userIdentityStore,
        TopicsStore topicsStore,
        MessagesStore messagesStore,
        IMessagePipeline pipeline,
        SpaceStore spaceStore,
        ILogger<InitializationEffect> logger)
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
        _logger = logger;

        dispatcher.RegisterHandler<Initialize>(
            _ => HandleInitializeAsync().LogFaults(_logger, nameof(Initialize)));
        dispatcher.RegisterHandler<SelectUser>(
            action => RegisterUserAsync(action.UserId).LogFaults(_logger, nameof(SelectUser)));
    }

    public async Task HandleInitializeAsync()
    {
        await _connectionService.ConnectAsync();
        _eventSubscriber.Subscribe();

        await RegisterUserAsync();

        // Validate and join space (must happen before push subscribe so space context is set)
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

        // Best-effort, network/browser-dependent — must not gate the rest of init
        // (agent list, topics). Push subscription still runs after space join so the
        // server can associate it with the space context. A slow pushManager.subscribe()
        // previously stalled the agent list ~30s by being awaited here.
        SubscribePushAsync().LogFaults(_logger, "push subscription");

        // Re-register user on reconnection (after initial subscribe to avoid race)
        _connectionService.OnReconnected += () =>
        {
            var registerTask = RegisterUserAsync();
            var joinTask = _topicService.JoinSpaceAsync(_spaceStore.State.CurrentSlug);

            // Re-send existing push subscription without force-refreshing the push channel.
            // Using RequestAndSubscribeAsync here would unsubscribe+resubscribe, generating a
            // new endpoint in Chrome and losing accumulated space memberships.
            var pushTask = _pushNotificationService.ResubscribeAsync()
                .ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);

            return Task.WhenAll(registerTask, joinTask, pushTask);
        };

        var agents = await _agentService.GetAgentsAsync();
        _dispatcher.Dispatch(new SetAgents(agents));
        await AgentSettingsEffect.LoadAsync(agents, _localStorage, _dispatcher);

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

        var serverTopics = await _topicService.GetAllTopicsAsync(agentToSelect.Id, spaceSlug);
        var topics = serverTopics.Select(StoredTopic.FromMetadata).ToList();
        _dispatcher.Dispatch(new TopicsLoaded(topics));

        // Gathered rather than detached: awaiting first-load init has to mean history is in
        // the store, or a caller that awaits it still races the messages it asked for.
        await Task.WhenAll(topics.Select(LoadTopicHistoryAsync));
    }

    public async Task RegisterUserAsync(string? userId = null)
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

        // Detached on purpose: a resumed stream is long-lived, so awaiting it would mean
        // awaiting the conversation.
        _streamResumeService.TryResumeStreamAsync(topic).LogFaults(_logger, "stream resume");
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