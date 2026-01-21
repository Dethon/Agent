using System.Net.Http.Json;
using System.Text.Json;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.State.Effects;

public sealed class UserIdentityEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly HttpClient _http;
    private readonly ILocalStorageService _localStorage;
    private const string StorageKey = "selectedUserId";

    public UserIdentityEffect(
        Dispatcher dispatcher,
        HttpClient http,
        ILocalStorageService localStorage)
    {
        _dispatcher = dispatcher;
        _http = http;
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
            var users = await _http.GetFromJsonAsync<List<UserConfig>>("users.json");
            _dispatcher.Dispatch(new UsersLoaded(users ?? []));

            var savedUserId = await _localStorage.GetAsync(StorageKey);
            if (!string.IsNullOrEmpty(savedUserId) && users?.Any(u => u.Id == savedUserId) == true)
            {
                _dispatcher.Dispatch(new SelectUser(savedUserId));
            }
        }
        catch (HttpRequestException)
        {
            _dispatcher.Dispatch(new UsersLoaded([]));
        }
        catch (JsonException)
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
