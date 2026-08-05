using WebChat.Client.Models;

namespace WebChat.Client.State.UserIdentity;

public record LoadUsers : IAction;

public record UsersLoaded(IReadOnlyList<UserConfig> Users) : IAction;

public record SelectUser(string UserId) : IAction;

public record ClearUser : IAction;

public sealed class UserIdentityStore : IDisposable
{
    private readonly Store<UserIdentityState> _store;

    public UserIdentityStore(Dispatcher dispatcher)
    {
        _store = new Store<UserIdentityState>(UserIdentityState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public UserIdentityState State => _store.State;

    public IObservable<UserIdentityState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static UserIdentityState Reduce(UserIdentityState state, IAction action) => action switch
    {
        LoadUsers => state with { IsLoading = true },
        UsersLoaded a => state with { AvailableUsers = a.Users, IsLoading = false },
        SelectUser a => state with { SelectedUserId = a.UserId },
        ClearUser => state with { SelectedUserId = null },
        _ => state
    };
}