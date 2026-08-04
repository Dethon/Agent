using Microsoft.AspNetCore.Components;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class SpaceEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ITopicService _topicService;
    private readonly IChatLiveConnection _liveConnection;
    private readonly IConfigService _configService;
    private readonly NavigationManager _navigationManager;
    private readonly IPushSubscriptionService _pushNotificationService;
    private readonly ILogger<SpaceEffect> _logger;
    private readonly IDisposable _handlerRegistration;
    private string _previousSlug = "default";

    public SpaceEffect(
        Dispatcher dispatcher,
        ITopicService topicService,
        IChatLiveConnection liveConnection,
        IConfigService configService,
        NavigationManager navigationManager,
        IPushSubscriptionService pushNotificationService,
        ILogger<SpaceEffect> logger)
    {
        _dispatcher = dispatcher;
        _topicService = topicService;
        _liveConnection = liveConnection;
        _configService = configService;
        _navigationManager = navigationManager;
        _pushNotificationService = pushNotificationService;
        _logger = logger;

        _handlerRegistration = dispatcher.RegisterHandler<SelectSpace>(
            action => HandleSelectSpaceAsync(action.Slug).LogFaults(_logger, nameof(SelectSpace)));
    }

    public async Task HandleSelectSpaceAsync(string slug)
    {
        var previousSlug = _previousSlug;
        _previousSlug = slug;

        if (slug == previousSlug)
        {
            return;
        }

        var space = await _configService.GetSpaceAsync(slug);
        if (space is null)
        {
            // If hub isn't connected yet, skip — InitializationEffect handles initial join
            if (!_liveConnection.IsConnected)
            {
                return;
            }

            _dispatcher.Dispatch(new InvalidSpace());
            _navigationManager.NavigateTo("/", replace: true);
            return;
        }

        await _topicService.JoinSpaceAsync(slug);

        try
        { await _pushNotificationService.ResubscribeAsync(); }
        catch { /* best-effort — don't block space transition */ }

        _dispatcher.Dispatch(new TopicsLoaded([]));
        _dispatcher.Dispatch(new ClearAllMessages());
        _dispatcher.Dispatch(new SpaceValidated(slug, space.Name, space.AccentColor));
    }

    public void Dispose()
    {
        _handlerRegistration.Dispose();
    }
}