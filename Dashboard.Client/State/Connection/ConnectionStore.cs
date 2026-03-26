namespace Dashboard.Client.State.Connection;

public record SetConnected(bool IsConnected) : IAction;

public sealed class ConnectionStore : Store<ConnectionState>
{
    public ConnectionStore() : base(new ConnectionState()) { }

    public void SetConnected(bool isConnected) =>
        Dispatch(new SetConnected(isConnected), static (_, a) => new ConnectionState { IsConnected = a.IsConnected });
}