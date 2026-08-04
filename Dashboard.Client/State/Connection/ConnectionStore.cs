namespace Dashboard.Client.State.Connection;

public record SetStatus(ConnectionStatus Status) : IAction;

public sealed class ConnectionStore : Store<ConnectionState>
{
    public ConnectionStore() : base(new ConnectionState()) { }

    public void SetConnecting() => SetStatus(ConnectionStatus.Connecting);

    public void SetLive() => SetStatus(ConnectionStatus.Live);

    public void SetReconnecting() => SetStatus(ConnectionStatus.Reconnecting);

    private void SetStatus(ConnectionStatus status) =>
        Dispatch(new SetStatus(status), static (state, action) => state with { Status = action.Status });
}