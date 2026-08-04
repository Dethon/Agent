using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeChatLiveConnection(CallRecorder? recorder = null) : IChatLiveConnection
{
    public int ConnectCalls { get; private set; }

    public HubConnection? HubConnection => null;

    public Task ConnectAsync()
    {
        ConnectCalls++;
        recorder?.Record("connect");
        return Task.CompletedTask;
    }

    public Task ReconnectIfNeededAsync() => Task.CompletedTask;

    // Not live throughout: this fake stands in for the whole live connection in tests that
    // are about something else. A test about a hub call uses the real live connection over
    // FakeHubConnection instead.
    public Task<HubResult<T>> InvokeAsync<T>(string methodName, params object?[] args) =>
        Task.FromResult(HubResult<T>.NotLive);

    public Task<HubResult<Nothing>> InvokeAsync(string methodName, params object?[] args) =>
        Task.FromResult(HubResult<Nothing>.NotLive);

    public Task<HubResult<IAsyncEnumerable<T>>> StreamAsync<T>(string methodName, params object?[] args) =>
        Task.FromResult(HubResult<IAsyncEnumerable<T>>.NotLive);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}