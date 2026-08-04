using Microsoft.AspNetCore.SignalR.Client;

namespace WebChat.Client.Contracts;

public interface IChatLiveConnection : IAsyncDisposable
{
    bool IsConnected { get; }
    HubConnection? HubConnection { get; }

    event Action? OnStateChanged;
    event Action? OnReconnecting;

    Task ConnectAsync();
    Task ReconnectIfNeededAsync();
}