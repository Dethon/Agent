namespace WebChat.Client.State.Connection;

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}

public sealed record ConnectionState
{
    public ConnectionStatus Status { get; init; } = ConnectionStatus.Disconnected;
    public DateTime? LastConnected { get; init; }
    public int ReconnectAttempts { get; init; }
    public string? Error { get; init; }

    public static ConnectionState Initial => new();
}
