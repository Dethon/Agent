using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.State.Connection;

namespace WebChat.Client.State.Effects;

// Session recovery runs on becoming live again, keyed on the connection epoch, the same way
// catch-up is. Driving it from here rather than from inside the live connection keeps the
// connection from depending on the services recovery calls back through it.
public sealed class SessionRecoveryEffect : IDisposable
{
    private readonly IDisposable _subscription;

    public SessionRecoveryEffect(
        ConnectionStore connectionStore,
        ISessionRecovery sessionRecovery,
        ILogger<SessionRecoveryEffect> logger)
    {
        _subscription = connectionStore.BecameLiveAgain.Subscribe(_ =>
            // Detached and logged: this runs on a connection that may already be dying, and a
            // failed hub call must not surface on whatever triggered the reconnect.
            sessionRecovery.RecoverAsync().LogFaults(logger, nameof(ISessionRecovery)));
    }

    public void Dispose() => _subscription.Dispose();
}