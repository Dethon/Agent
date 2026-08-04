using Microsoft.AspNetCore.SignalR.Client;

namespace WebChat.Client.Contracts;

public interface IChatLiveConnection : IAsyncDisposable
{
    // Retained on purpose and temporarily: the callers that have not moved onto the verbs
    // below still reach through it. Removing it is the last ticket of this feature.
    HubConnection? HubConnection { get; }

    Task ConnectAsync();
    Task ReconnectIfNeededAsync();

    Task<HubResult<T>> InvokeAsync<T>(string methodName, params object?[] args);
    Task<HubResult<Nothing>> InvokeAsync(string methodName, params object?[] args);
    Task<HubResult<IAsyncEnumerable<T>>> StreamAsync<T>(string methodName, params object?[] args);
}