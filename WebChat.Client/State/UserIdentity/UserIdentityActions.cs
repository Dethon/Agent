using WebChat.Client.Models;

namespace WebChat.Client.State.UserIdentity;

public record LoadUsers : IAction;

public record UsersLoaded(IReadOnlyList<UserConfig> Users) : IAction;

public record SelectUser(string UserId) : IAction;

public record ClearUser : IAction;
