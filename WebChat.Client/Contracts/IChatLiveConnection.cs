namespace WebChat.Client.Contracts;

public interface IChatLiveConnection : IAsyncDisposable
{
    Task ConnectAsync();
    Task ReconnectIfNeededAsync();

    Task<HubResult<T>> InvokeAsync<T>(string methodName, params object?[] args);
    Task<HubResult<Nothing>> InvokeAsync(string methodName, params object?[] args);
    Task<HubResult<IAsyncEnumerable<T>>> StreamAsync<T>(string methodName, params object?[] args);
}