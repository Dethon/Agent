namespace WebChat.Client.State.Connection;

public record ConnectionConnecting : IAction;

public record ConnectionConnected : IAction;

public record ConnectionReconnecting : IAction;

public record ConnectionReconnected : IAction;

public record ConnectionClosed(string? Error) : IAction;