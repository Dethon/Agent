namespace WebChat.Client.State.Connection;

public record ConnectionStatusChanged(ConnectionStatus Status) : IAction;

public record ConnectionConnecting : IAction;

public record ConnectionConnected : IAction;

public record ConnectionReconnecting : IAction;

public record ConnectionReconnected : IAction;

public record ConnectionClosed(string? Error) : IAction;

public record ConnectionError(string Error) : IAction;