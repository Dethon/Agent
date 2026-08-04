using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.State;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Effects;

namespace Tests.Unit.WebChat.Client.State;

public sealed class SessionRecoveryEffectTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly ConnectionStore _connectionStore;
    private readonly FakeSessionRecovery _recovery = new();
    private readonly RecordingLogger<SessionRecoveryEffect> _logger = new();
    private readonly SessionRecoveryEffect _effect;

    public SessionRecoveryEffectTests()
    {
        _connectionStore = new ConnectionStore(_dispatcher);
        _effect = new SessionRecoveryEffect(_connectionStore, _recovery, _logger);
    }

    [Fact]
    public async Task FirstConnection_DoesNotRecover()
    {
        _dispatcher.Dispatch(new ConnectionConnected());

        await Task.Delay(50);

        _recovery.RecoverCalls.ShouldBe(0);
    }

    [Fact]
    public async Task BecomingLiveAgain_Recovers()
    {
        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionClosed(null));

        _dispatcher.Dispatch(new ConnectionConnected());

        await TestChat.Eventually(() => _recovery.RecoverCalls == 1);
    }

    [Fact]
    public async Task ReconnectingTransport_Recovers()
    {
        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionReconnecting());

        _dispatcher.Dispatch(new ConnectionReconnected());

        await TestChat.Eventually(() => _recovery.RecoverCalls == 1);
    }

    // A rebuild fast enough that nobody observed the gap still has to re-identify the client.
    [Fact]
    public async Task RebuiltWithoutAnObservedDisconnect_Recovers()
    {
        _dispatcher.Dispatch(new ConnectionConnected());

        _dispatcher.Dispatch(new ConnectionConnected());

        await TestChat.Eventually(() => _recovery.RecoverCalls == 1);
    }

    [Fact]
    public async Task StatusChangesThatDoNotAdvanceTheEpoch_DoNotRecover()
    {
        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionClosed(null));
        _dispatcher.Dispatch(new ConnectionConnecting());
        _dispatcher.Dispatch(new ConnectionReconnecting());

        await Task.Delay(50);

        _recovery.RecoverCalls.ShouldBe(0);
    }

    // Recovery runs on a connection that may already be dying, so a failed hub call has to
    // land in the log rather than on whatever triggered the reconnect.
    [Fact]
    public async Task RecoveryFails_TheFaultIsLoggedRatherThanThrown()
    {
        _recovery.ThrowOnRecover = new InvalidOperationException("hub call failed");
        _dispatcher.Dispatch(new ConnectionConnected());

        _dispatcher.Dispatch(new ConnectionConnected());

        var entry = await _logger.WaitForEntryAsync();
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("hub call failed");
    }

    [Fact]
    public async Task Disposed_StopsRecovering()
    {
        _dispatcher.Dispatch(new ConnectionConnected());
        _effect.Dispose();

        _dispatcher.Dispatch(new ConnectionConnected());

        await Task.Delay(50);
        _recovery.RecoverCalls.ShouldBe(0);
    }

    public void Dispose()
    {
        _effect.Dispose();
        _connectionStore.Dispose();
    }
}