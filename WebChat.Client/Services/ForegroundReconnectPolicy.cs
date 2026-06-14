using Microsoft.AspNetCore.SignalR.Client;

namespace WebChat.Client.Services;

public enum ForegroundAction
{
    NoOp,
    Probe,
    Rebuild
}

public static class ForegroundReconnectPolicy
{
    public static ForegroundAction Decide(HubConnectionState? state) => state switch
    {
        // Reports Connected after an Android background freeze, but the transport may be a
        // half-open zombie that no close event ever fired for. Don't trust it — probe.
        HubConnectionState.Connected => ForegroundAction.Probe,
        // An attempt is already in flight (initial connect or auto-reconnect's 1s retry loop);
        // tearing it down would interrupt an in-progress recovery.
        HubConnectionState.Connecting or HubConnectionState.Reconnecting => ForegroundAction.NoOp,
        // null (disposed/never built) or Disconnected — nothing is trying, so build a connection.
        _ => ForegroundAction.Rebuild
    };
}