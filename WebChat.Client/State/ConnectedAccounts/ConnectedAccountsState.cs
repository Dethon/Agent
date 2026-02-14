namespace WebChat.Client.State.ConnectedAccounts;

public sealed record ConnectedAccountsState
{
    public IReadOnlyDictionary<string, ProviderStatus> Providers { get; init; }
        = new Dictionary<string, ProviderStatus>();

    public static ConnectedAccountsState Initial => new();
}

public record ProviderStatus(bool Connected, string? Email = null);
