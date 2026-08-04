using System.ComponentModel;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Channels;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.McpServers;

namespace Tests.Integration.Channels;

// A connection is started once and runs for its lifetime: connect, register the catalog, watch
// health, reconnect, re-register. The order lives in the thing the order is about, so this drives
// the real connection against a real server rather than asserting on a host's sequencing.
public class McpChannelConnectionRunTests
{
    private static readonly SemaphoreSlim _registered = new(0);
    private static int _registerCalls;
    private static int _unhealthy;

    private static readonly IReadOnlyList<AgentCatalogEntry> _catalog =
        [new AgentCatalogEntry("jonas", "Jonas", "general")];

    [Fact]
    public async Task RunAsync_RegistersTheCatalogAfterConnecting_AndAgainAfterAReconnect()
    {
        await ResetAsync();

        await using var server = await StartServerAsync();
        await using var connection = new McpChannelConnection(
            "test", healthCheckInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = connection.RunAsync(server.Endpoint, _catalog, cts.Token);

        await _registered.WaitAsync(cts.Token);
        Volatile.Read(ref _registerCalls).ShouldBe(1);

        // The next health tick finds the server unreachable, which is what drives a reconnect. It
        // recovers immediately afterwards, so the reconnect succeeds and re-registers.
        Interlocked.Exchange(ref _unhealthy, 1);

        await _registered.WaitAsync(cts.Token);
        Volatile.Read(ref _registerCalls).ShouldBeGreaterThanOrEqualTo(2);

        await cts.CancelAsync();
        await run;
    }


    [Fact]
    public async Task RunAsync_TheServerIsNotThereYet_KeepsRetryingUntilItIs()
    {
        // A channel server that starts after the agent must still get connected to, so the first
        // connect backs off and retries rather than giving up.
        await ResetAsync();
        var port = TestPort.GetAvailable();
        var endpoint = $"http://localhost:{port}/mcp";

        await using var connection = new McpChannelConnection(
            "test", healthCheckInterval: TimeSpan.FromMilliseconds(50));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = connection.RunAsync(endpoint, _catalog, cts.Token);

        await using var server = await StartServerAsync(port);

        await _registered.WaitAsync(cts.Token);

        await cts.CancelAsync();
        await run;
    }

    private static async Task ResetAsync()
    {
        Interlocked.Exchange(ref _registerCalls, 0);
        Interlocked.Exchange(ref _unhealthy, 0);
        while (_registered.CurrentCount > 0)
        {
            await _registered.WaitAsync();
        }
    }

    private static Task<RunningServer> StartServerAsync(int? port = null) =>
        InMemoryMcpServer.StartAsync(services => services
        .AddMcpServer()
        .WithHttpTransport()
        .WithTools<RegisterTools>()
        .WithRequestFilters(filters => filters.AddListToolsFilter(next => (context, ct) =>
        {
            // One failed health ping, then healthy again.
            if (Interlocked.Exchange(ref _unhealthy, 0) == 1)
            {
                throw new InvalidOperationException("the channel server went away");
            }
            return next(context, ct);
        })), port);

    [McpServerToolType]
    public sealed class RegisterTools
    {
        [McpServerTool(Name = ChannelProtocol.RegisterAgentsTool)]
        [Description("Take the agent catalog.")]
        public static string Register(IReadOnlyList<AgentCatalogEntry> agents)
        {
            Interlocked.Increment(ref _registerCalls);
            _registered.Release();
            return "ok";
        }
    }
}