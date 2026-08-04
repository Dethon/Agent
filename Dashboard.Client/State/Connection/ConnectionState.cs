namespace Dashboard.Client.State.Connection;

// There is no permanent disconnected state: the live connection never stops trying, so the honest
// distinction is between never having been up and having lost it.
public enum ConnectionStatus
{
    Connecting,
    Live,
    Reconnecting
}

public record ConnectionState
{
    public ConnectionStatus Status { get; init; } = ConnectionStatus.Connecting;
}