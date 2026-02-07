namespace WebChat.Client.State.UserIdentity;

public static class UserIdentityReducers
{
    public static UserIdentityState Reduce(UserIdentityState state, IAction action) => action switch
    {
        LoadUsers => state with { IsLoading = true },
        UsersLoaded a => state with { AvailableUsers = a.Users, IsLoading = false },
        SelectUser a => state with { SelectedUserId = a.UserId },
        ClearUser => state with { SelectedUserId = null },
        _ => state
    };
}
