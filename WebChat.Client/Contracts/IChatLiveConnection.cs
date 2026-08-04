using Microsoft.AspNetCore.SignalR.Client;

namespace WebChat.Client.Contracts;

public interface IChatLiveConnection : IAsyncDisposable
{
    // Retained on purpose and temporarily: the services that make hub calls still reach
    // through it. Removing it is candidate 5's work.
    HubConnection? HubConnection { get; }

    Task ConnectAsync();
    Task ReconnectIfNeededAsync();
}