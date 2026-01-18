namespace WebChat.Client.Contracts;

public interface IChatConnectionService : IAsyncDisposable
{
    bool IsConnected { get; }
    bool IsReconnecting { get; }

    event Action? OnStateChanged;
    event Func<Task>? OnReconnected;
    event Action? OnReconnecting;

    Task ConnectAsync();
}