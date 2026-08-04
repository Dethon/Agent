using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeChatLiveConnection(CallRecorder? recorder = null) : IChatLiveConnection
{
    public int ConnectCalls { get; private set; }

    public bool IsConnected { get; set; }

    // There is no hub in a unit test, so user registration cannot be observed through an
    // invocation. Reaching for the connection is the observable half of that step, and it
    // only happens when the effect has a user to register.
    public HubConnection? HubConnection
    {
        get
        {
            recorder?.Record("register-user");
            return null;
        }
    }

    public event Action? OnStateChanged;
    public event Func<Task>? OnReconnected;
    public event Action? OnReconnecting;

    public Task ConnectAsync()
    {
        ConnectCalls++;
        IsConnected = true;
        recorder?.Record("connect");
        return Task.CompletedTask;
    }

    public Task ReconnectIfNeededAsync() => Task.CompletedTask;

    public Task RaiseReconnectedAsync() => OnReconnected?.Invoke() ?? Task.CompletedTask;

    public void RaiseReconnecting() => OnReconnecting?.Invoke();

    public void RaiseStateChanged() => OnStateChanged?.Invoke();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}