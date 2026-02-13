using Microsoft.AspNetCore.Components;
using WebChat.Client.Contracts;
using WebChat.Client.Services;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class SpaceEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ITopicService _topicService;
    private readonly IChatConnectionService _connectionService;
    private readonly ConfigService _configService;
    private readonly NavigationManager _navigationManager;
    private readonly PushNotificationService _pushNotificationService;
    private readonly IDisposable _handlerRegistration;
    private string _previousSlug = "default";

    public SpaceEffect(
        Dispatcher dispatcher,
        ITopicService topicService,
        IChatConnectionService connectionService,
        ConfigService configService,
        NavigationManager navigationManager,
        PushNotificationService pushNotificationService)
    {
        _dispatcher = dispatcher;
        _topicService = topicService;
        _connectionService = connectionService;
        _configService = configService;
        _navigationManager = navigationManager;
        _pushNotificationService = pushNotificationService;

        _handlerRegistration = dispatcher.RegisterHandler<SelectSpace>(HandleSelectSpace);
    }

    private void HandleSelectSpace(SelectSpace action)
    {
        var previousSlug = _previousSlug;
        _previousSlug = action.Slug;
        _ = HandleSelectSpaceAsync(action.Slug, previousSlug);
    }

    private async Task HandleSelectSpaceAsync(string slug, string previousSlug)
    {
        if (slug == previousSlug)
        {
            return;
        }

        var space = await _configService.GetSpaceAsync(slug);
        if (space is null)
        {
            // If hub isn't connected yet, skip — InitializationEffect handles initial join
            if (!_connectionService.IsConnected)
            {
                return;
            }

            // Space is invalid — redirect to default
            _dispatcher.Dispatch(new InvalidSpace());
            _navigationManager.NavigateTo("/", replace: true);
            return;
        }

        // Join SignalR group for the space
        await _topicService.JoinSpaceAsync(slug);

        // Re-register push subscription for the new space (best-effort)
        try { await _pushNotificationService.ResubscribeAsync(); }
        catch { /* best-effort — don't block space transition */ }

        // Clear topics and messages for space transition
        _dispatcher.Dispatch(new TopicsLoaded([]));
        _dispatcher.Dispatch(new ClearAllMessages());
        _dispatcher.Dispatch(new SpaceValidated(slug, space.Name, space.AccentColor));
    }

    public void Dispose()
    {
        _handlerRegistration.Dispose();
    }
}
