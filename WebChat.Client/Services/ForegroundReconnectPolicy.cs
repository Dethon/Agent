using Microsoft.AspNetCore.SignalR.Client;

namespace WebChat.Client.Services;

public enum ForegroundAction
{
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
        // Anything else — including Connecting/Reconnecting: an in-flight attempt thawed from
        // a background freeze can hang on a dead handshake for tens of seconds, so replace it
        // with a fresh connection instead of waiting it out.
        _ => ForegroundAction.Rebuild
    };
}