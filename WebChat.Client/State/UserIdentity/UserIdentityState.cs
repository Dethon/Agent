using WebChat.Client.Models;

namespace WebChat.Client.State.UserIdentity;

public sealed record UserIdentityState
{
    public string? SelectedUserId { get; init; }
    public IReadOnlyList<UserConfig> AvailableUsers { get; init; } = [];
    public bool IsLoading { get; init; }

    public static UserIdentityState Initial => new();
}
