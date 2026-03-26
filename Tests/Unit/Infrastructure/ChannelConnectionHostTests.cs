using Agent.App;
using Agent.Settings;
using Infrastructure.Clients.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class ChannelConnectionHostTests
{
    private readonly NullLogger<ChannelConnectionHost> _logger = new();

    [Fact]
    public async Task ConnectsToChannel_OnStartup()
    {
        var fake = new FakeMcpChannelConnection("ch-1");
        var endpoints = new[] { new ChannelEndpoint { ChannelId = "ch-1", Endpoint = "http://localhost:9999" } };
        var sut = new ChannelConnectionHost(endpoints, [fake], _logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        _ = sut.StartAsync(cts.Token);

        await fake.WaitForConnectAsync(cts.Token);
        fake.ConnectCount.ShouldBe(1);
    }

    [Fact]
    public async Task ReconnectsAfterHealthCheckFailure()
    {
        var fake = new FakeMcpChannelConnection("ch-1");
        var endpoints = new[] { new ChannelEndpoint { ChannelId = "ch-1", Endpoint = "http://localhost:9999" } };
        var sut = new ChannelConnectionHost(
            endpoints, [fake], _logger,
            healthCheckInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = sut.StartAsync(cts.Token);

        // Wait for initial connect
        await fake.WaitForConnectAsync(cts.Token);
        fake.ConnectCount.ShouldBe(1);

        // Simulate connection drop
        fake.SetHealthy(false);

        // Wait for reconnect
        await fake.WaitForConnectCountAsync(2, cts.Token);
        fake.ConnectCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task RetriesConnectionOnInitialFailure()
    {
        var fake = new FakeMcpChannelConnection("ch-1");
        fake.FailNextConnects(2);
        var endpoints = new[] { new ChannelEndpoint { ChannelId = "ch-1", Endpoint = "http://localhost:9999" } };
        var sut = new ChannelConnectionHost(endpoints, [fake], _logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _ = sut.StartAsync(cts.Token);

        await fake.WaitForConnectAsync(cts.Token);
        fake.ConnectAttempts.ShouldBeGreaterThanOrEqualTo(3);
        fake.ConnectCount.ShouldBe(1);
    }

    [Fact]
    public async Task StopsOnCancellation()
    {
        var fake = new FakeMcpChannelConnection("ch-1");
        var endpoints = new[] { new ChannelEndpoint { ChannelId = "ch-1", Endpoint = "http://localhost:9999" } };
        var sut = new ChannelConnectionHost(
            endpoints, [fake], _logger,
            healthCheckInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        var task = sut.StartAsync(cts.Token);

        await fake.WaitForConnectAsync(cts.Token);

        await cts.CancelAsync();
        await sut.StopAsync(CancellationToken.None);

        // Should not throw
        await task;
    }
}

internal sealed class FakeMcpChannelConnection(string channelId) : IMcpChannelConnection
{
    private readonly TaskCompletionSource _firstConnect = new();
    private readonly SemaphoreSlim _connectSignal = new(0);
    private int _healthy = 1;
    private int _failNextConnects;

    private int _connectCount;
    private int _connectAttempts;

    public string ChannelId { get; } = channelId;
    public int ConnectCount => Volatile.Read(ref _connectCount);
    public int ConnectAttempts => Volatile.Read(ref _connectAttempts);

    public Task ConnectAsync(string endpoint, CancellationToken ct)
    {
        Interlocked.Increment(ref _connectAttempts);
        if (Interlocked.Decrement(ref _failNextConnects) >= 0)
        {
            throw new HttpRequestException("Simulated connection failure");
        }

        Interlocked.Increment(ref _connectCount);
        _firstConnect.TrySetResult();
        _connectSignal.Release();
        return Task.CompletedTask;
    }

    public Task<bool> IsHealthyAsync(CancellationToken ct) =>
        Task.FromResult(Interlocked.CompareExchange(ref _healthy, 0, 0) == 1);

    public Task ReconnectAsync(string endpoint, CancellationToken ct) => ConnectAsync(endpoint, ct);

    public void SetHealthy(bool healthy) => Interlocked.Exchange(ref _healthy, healthy ? 1 : 0);

    public void FailNextConnects(int count) => Interlocked.Exchange(ref _failNextConnects, count);

    public Task WaitForConnectAsync(CancellationToken ct) => _firstConnect.Task.WaitAsync(ct);

    public async Task WaitForConnectCountAsync(int count, CancellationToken ct)
    {
        while (ConnectCount < count)
        {
            await _connectSignal.WaitAsync(ct);
        }
    }
}