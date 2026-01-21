using Microsoft.AspNetCore.SignalR.Client;

namespace WebChat.Client.Contracts;

public interface IChatConnectionService : IAsyncDisposable
{
    bool IsConnected { get; }
    HubConnection? HubConnection { get; }

    event Action? OnStateChanged;
    event Func<Task>? OnReconnected;
    event Action? OnReconnecting;

    Task ConnectAsync();
}