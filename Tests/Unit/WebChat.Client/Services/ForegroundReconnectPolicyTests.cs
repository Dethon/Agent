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
    [InlineData(HubConnectionState.Connecting, ForegroundAction.Rebuild)]   // in-flight attempt may be a thawed zombie -> replace it
    [InlineData(HubConnectionState.Reconnecting, ForegroundAction.Rebuild)] // ditto: a frozen retry can hang for tens of seconds
    public void Decide_ForConnectionState_ReturnsExpectedAction(
        HubConnectionState? state, ForegroundAction expected)
    {
        ForegroundReconnectPolicy.Decide(state).ShouldBe(expected);
    }
}