using System.Net;
using System.Text.Json;
using Domain.Channels;
using Domain.DTOs.Channel;
using McpChannelSignalR.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Tests.Integration.Fixtures;
// Aliased because Tests.Integration has sibling namespaces named after the channel projects
// (Tests.Integration.McpChannelSignalR), which shadow the project namespaces from inside Tests.
using SignalRChannel = McpChannelSignalR.McpTools;
using SignalRSettings = McpChannelSignalR.Settings;

namespace Tests.Integration.Channels;

public class ChannelReceiveContractTests
{
    // ConfigureChannel eagerly calls ConnectionMultiplexer.Connect. abortConnect=false makes that
    // hand back a disconnected multiplexer instead of throwing, so the registration theory runs
    // without a Redis container; port 1 is refused instantly and the short timeouts keep the
    // background reconnect loop from lingering.
    private const string UnreachableRedis =
        "127.0.0.1:1,abortConnect=false,connectTimeout=100,connectRetry=1";

    // One row per channel server. Later channel migrations each append exactly one row; the test
    // body is written once and never copied.
    public static TheoryData<string, Action<IMcpServerBuilder>> Servers => new()
    {
        { "signalr", b => b.WithTools<SignalRChannel.ChannelReceiveTool>() }
    };

    // One row per channel server, driving that server's REAL registration entry point. The
    // contract test hand-registers the tool and the inbox, so it stays green against a ConfigModule
    // that forgot .WithTools<ChannelReceiveTool>() or .AddSingleton<ChannelInbox>() — which would
    // ship a silently dead channel. This theory is what catches that.
    public static TheoryData<string, Action<IServiceCollection>> Registrations => new()
    {
        {
            "signalr",
            services => services.ConfigureChannel(
                new SignalRSettings.ChannelSettings { RedisConnectionString = UnreachableRedis })
        }
    };

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task EnqueuedMessage_IsDeliveredToAPollingClient(
        string channelId, Action<IMcpServerBuilder> registerTool)
    {
        var subscriberId = ChannelProtocol.ChannelClientNamePrefix + channelId;
        var port = TestPort.GetAvailable();
        var inbox = new ChannelInbox();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.AddSingleton(inbox);
        registerTool(builder.Services.AddMcpServer().WithHttpTransport());

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        try
        {
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"http://localhost:{port}/mcp")
                }));

            // Register the subscriber, then enqueue while a poll is in flight.
            //
            // Proven here: an item enqueued server-side reaches a client polling over real
            // HTTP + MCP, and survives the ChannelReceiveResult round trip. NOT proven here: the
            // long-poll wake. The delay below is a best-effort guess at "the poll has arrived"; if
            // it hasn't, the enqueue lands first and the poll returns through the pending-items
            // fast path — still green. The wake itself is pinned deterministically on a fake clock
            // by ChannelInboxTests.ReceiveAsync_WhenEmpty_WakesOnEnqueue. Do not read this sleep as
            // the authority on long-poll behaviour.
            await Poll(client, subscriberId, maxWaitMs: 0);

            var pending = Poll(client, subscriberId, maxWaitMs: 10_000);
            await Task.Delay(200);
            inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
            {
                ConversationId = "conv-1",
                Sender = "user",
                Content = "hello"
            }));

            var result = await pending;

            result.Items.Count.ShouldBe(1);
            result.Items[0].Kind.ShouldBe(ChannelInboxItemKind.Message);
            result.Items[0].Message!.Content.ShouldBe("hello");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public void ChannelServerRegistration_ExposesReceiveToolAndInbox(
        string channelId, Action<IServiceCollection> configureChannel)
    {
        var services = new ServiceCollection();
        configureChannel(services);

        using var provider = services.BuildServiceProvider();

        provider.GetService<ChannelInbox>()
            .ShouldNotBeNull($"{channelId} must register a ChannelInbox singleton");
        provider.GetServices<McpServerTool>()
            .Select(tool => tool.ProtocolTool.Name)
            .ShouldContain(
                ChannelProtocol.ReceiveTool,
                $"{channelId} must expose {ChannelProtocol.ReceiveTool} from its own ConfigModule");
    }

    private static async Task<ChannelReceiveResult> Poll(McpClient client, string subscriberId, int maxWaitMs)
    {
        var call = await client.CallToolAsync(
            ChannelProtocol.ReceiveTool,
            new Dictionary<string, object?>
            {
                ["subscriberId"] = subscriberId,
                ["maxWaitMs"] = maxWaitMs
            });

        var text = call.Content.OfType<TextContentBlock>().First().Text;
        return JsonSerializer.Deserialize<ChannelReceiveResult>(text, ChannelProtocol.SerializerOptions)!;
    }
}