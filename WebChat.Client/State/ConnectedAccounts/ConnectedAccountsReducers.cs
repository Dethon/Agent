namespace WebChat.Client.State.ConnectedAccounts;

public static class ConnectedAccountsReducers
{
    public static ConnectedAccountsState Reduce(ConnectedAccountsState state, IAction action) => action switch
    {
        AccountStatusLoaded loaded => SetProvider(state, loaded.Provider, new ProviderStatus(loaded.Connected, loaded.Email)),
        AccountConnected connected => SetProvider(state, connected.Provider, new ProviderStatus(true, connected.Email)),
        AccountDisconnected disconnected => SetProvider(state, disconnected.Provider, new ProviderStatus(false)),
        _ => state
    };

    private static ConnectedAccountsState SetProvider(ConnectedAccountsState state, string provider, ProviderStatus status)
    {
        var providers = new Dictionary<string, ProviderStatus>(state.Providers) { [provider] = status };
        return state with { Providers = providers };
    }
}
