using Microsoft.AspNetCore.Components;
using WebChat.Client.Contracts;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class SpaceEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ITopicService _topicService;
    private readonly IChatConnectionService _connectionService;
    private readonly NavigationManager _navigationManager;
    private string _lastValidatedSlug = "default";

    public SpaceEffect(
        Dispatcher dispatcher,
        ITopicService topicService,
        IChatConnectionService connectionService,
        NavigationManager navigationManager)
    {
        _dispatcher = dispatcher;
        _topicService = topicService;
        _connectionService = connectionService;
        _navigationManager = navigationManager;

        dispatcher.RegisterHandler<SelectSpace>(HandleSelectSpace);
    }

    private void HandleSelectSpace(SelectSpace action)
    {
        _ = HandleSelectSpaceAsync(action.Slug);
    }

    private async Task HandleSelectSpaceAsync(string slug)
    {
        if (slug == _lastValidatedSlug)
        {
            return;
        }

        var space = await _topicService.JoinSpaceAsync(slug);
        if (space is null)
        {
            // If hub isn't connected yet, skip — InitializationEffect handles initial join
            if (!_connectionService.IsConnected)
            {
                return;
            }

            // Hub is connected but space is invalid — redirect to default
            _dispatcher.Dispatch(new InvalidSpace());
            _lastValidatedSlug = "default";
            _navigationManager.NavigateTo("/", replace: true);
            return;
        }

        // Clear topics and messages for space transition
        _dispatcher.Dispatch(new TopicsLoaded([]));
        _dispatcher.Dispatch(new ClearAllMessages());
        _dispatcher.Dispatch(new SpaceValidated(slug, space.Name, space.AccentColor));
        _lastValidatedSlug = slug;
    }

    public void Dispose()
    {
        // No subscriptions to dispose
    }
}
