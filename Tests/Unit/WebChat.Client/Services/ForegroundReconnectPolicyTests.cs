using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using WebChat.Client.Services;

namespace Tests.Unit.WebChat.Client.Services;

public class ForegroundReconnectPolicyTests
{
    [Theory]
    [InlineData(null, ForegroundAction.Rebuild)]                            // no connection object -> build one
    [InlineData(HubConnectionState.Disconnected, ForegroundAction.Rebuild)] // known dead -> rebuild
    [InlineData(HubConnectionState.Connected, ForegroundAction.Probe)]      // maybe a zombie -> verify with a probe
    [InlineData(HubConnectionState.Connecting, ForegroundAction.NoOp)]      // initial connect in flight -> leave it
    [InlineData(HubConnectionState.Reconnecting, ForegroundAction.NoOp)]    // auto-reconnect already retrying -> leave it
    public void Decide_ForConnectionState_ReturnsExpectedAction(
        HubConnectionState? state, ForegroundAction expected)
    {
        ForegroundReconnectPolicy.Decide(state).ShouldBe(expected);
    }
}