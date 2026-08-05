using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.State.Effects;

public sealed class UserIdentityEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IConfigService _configService;
    private readonly ILocalStorageService _localStorage;
    private readonly ILogger<UserIdentityEffect> _logger;
    private readonly IDisposable _initializeRegistration;
    private readonly IDisposable _selectUserRegistration;
    private const string StorageKey = "selectedUserId";

    public UserIdentityEffect(
        Dispatcher dispatcher,
        IConfigService configService,
        ILocalStorageService localStorage,
        ILogger<UserIdentityEffect> logger)
    {
        _dispatcher = dispatcher;
        _configService = configService;
        _localStorage = localStorage;
        _logger = logger;

        _initializeRegistration = dispatcher.RegisterHandler<Initialize>(
            _ => LoadUsersAsync().LogFaults(_logger, nameof(Initialize)));
        _selectUserRegistration = dispatcher.RegisterHandler<SelectUser>(
            action => PersistSelectedUserAsync(action.UserId).LogFaults(_logger, nameof(SelectUser)));
    }

    public async Task LoadUsersAsync()
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

    public Task PersistSelectedUserAsync(string userId) => _localStorage.SetAsync(StorageKey, userId).AsTask();

    public void Dispose()
    {
        _initializeRegistration.Dispose();
        _selectUserRegistration.Dispose();
    }
}