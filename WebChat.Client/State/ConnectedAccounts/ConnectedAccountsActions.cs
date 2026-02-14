namespace WebChat.Client.State.ConnectedAccounts;

public record AccountStatusLoaded(string Provider, bool Connected, string? Email) : IAction;

public record AccountConnected(string Provider, string? Email) : IAction;

public record AccountDisconnected(string Provider) : IAction;
