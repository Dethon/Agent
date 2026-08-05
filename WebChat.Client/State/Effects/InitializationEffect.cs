using Domain.DTOs.Channel;
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
    private readonly IChatLiveConnection _liveConnection;
    private readonly IChatSessionService _sessionService;
    private readonly IAgentService _agentService;
    private readonly ITopicService _topicService;
    private readonly IConfigService _configService;
    private readonly ILocalStorageService _localStorage;
    private readonly IStreamResumeService _streamResumeService;
    private readonly IPushSubscriptionService _pushNotificationService;
    private readonly UserIdentityStore _userIdentityStore;
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly IMessagePipeline _pipeline;
    private readonly SpaceStore _spaceStore;
    private readonly ILogger<InitializationEffect> _logger;
    private bool _awaitingAgentCatalog;

    public InitializationEffect(
        Dispatcher dispatcher,
        IChatLiveConnection liveConnection,
        IChatSessionService sessionService,
        IAgentService agentService,
        ITopicService topicService,
        IConfigService configService,
        ILocalStorageService localStorage,
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
        _liveConnection = liveConnection;
        _sessionService = sessionService;
        _agentService = agentService;
        _topicService = topicService;
        _configService = configService;
        _localStorage = localStorage;
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
        dispatcher.RegisterHandler<SetAgents>(
            action => HandleAgentCatalogArrivedAsync(action.Agents).LogFaults(_logger, nameof(SetAgents)));
    }

    public async Task HandleInitializeAsync()
    {
        await _liveConnection.ConnectAsync();

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

        var catalog = await _agentService.GetAgentsAsync();
        if (!catalog.IsLive)
        {
            // Nothing else retries this fetch: the next SetAgents — the OnAgentsUpdated broadcast
            // when the agent re-registers, on this connection or the next epoch's — completes the
            // initialization this load could not.
            _awaitingAgentCatalog = true;
            return;
        }

        _dispatcher.Dispatch(new SetAgents(catalog.Value!));
        await SelectAgentAndLoadTopicsAsync(catalog.Value!);
    }

    // A catalog arriving after a first load that could not fetch it. The reducer has already stored
    // the agents; what first load never got to do — pick the agent and load its topics — happens
    // here, exactly once.
    private async Task HandleAgentCatalogArrivedAsync(IReadOnlyList<AgentCatalogEntry> agents)
    {
        if (!_awaitingAgentCatalog || agents.Count == 0)
        {
            return;
        }

        _awaitingAgentCatalog = false;
        await SelectAgentAndLoadTopicsAsync(agents);
    }

    private async Task SelectAgentAndLoadTopicsAsync(IReadOnlyList<AgentCatalogEntry> agents)
    {
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

        var serverTopics = await _topicService.GetAllTopicsAsync(agentToSelect.Id, _spaceStore.State.CurrentSlug);
        if (!serverTopics.IsLive)
        {
            return;
        }

        var topics = serverTopics.Value!.Select(StoredTopic.FromMetadata).ToList();
        _dispatcher.Dispatch(new TopicsLoaded(topics));

        // Gathered rather than detached: awaiting first-load init has to mean history is in
        // the store, or a caller that awaits it still races the messages it asked for.
        await Task.WhenAll(topics.Select(LoadTopicHistoryAsync));
    }

    public Task RegisterUserAsync(string? userId = null)
    {
        userId ??= _userIdentityStore.State.SelectedUserId;
        return string.IsNullOrEmpty(userId) ? Task.CompletedTask : _sessionService.RegisterUserAsync(userId);
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
        if (!history.IsLive)
        {
            return;
        }

        _pipeline.LoadHistory(topic.TopicId, history.Value!);

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