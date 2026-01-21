using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.Services;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.State.Effects;

public sealed class UserIdentityEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ConfigService _configService;
    private readonly ILocalStorageService _localStorage;
    private const string StorageKey = "selectedUserId";

    public UserIdentityEffect(
        Dispatcher dispatcher,
        ConfigService configService,
        ILocalStorageService localStorage)
    {
        _dispatcher = dispatcher;
        _configService = configService;
        _localStorage = localStorage;

        dispatcher.RegisterHandler<Initialize>(HandleInitialize);
        dispatcher.RegisterHandler<SelectUser>(HandleSelectUser);
    }

    private void HandleInitialize(Initialize action)
    {
        _ = LoadUsersAsync();
    }

    private async Task LoadUsersAsync()
    {
        _dispatcher.Dispatch(new LoadUsers());

        try
        {
            var config = await _configService.GetConfigAsync();
            var users = config.Users?.Select(u => new UserConfig(u.Id, u.AvatarUrl)).ToList() ?? [];
            _dispatcher.Dispatch(new UsersLoaded(users));

            var savedUserId = await _localStorage.GetAsync(StorageKey);
            if (!string.IsNullOrEmpty(savedUserId) && users.Any(u => u.Id == savedUserId))
            {
                _dispatcher.Dispatch(new SelectUser(savedUserId));
            }
        }
        catch (HttpRequestException)
        {
            _dispatcher.Dispatch(new UsersLoaded([]));
        }
    }

    private void HandleSelectUser(SelectUser action)
    {
        _ = _localStorage.SetAsync(StorageKey, action.UserId);
    }

    public void Dispose()
    {
        // No subscriptions to dispose
    }
}
