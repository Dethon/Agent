using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Domain.Channels;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Channels;
using Infrastructure.McpTools;
using McpChannelServiceBus.Modules;
using McpChannelSignalR.Modules;
using McpChannelTelegram.Modules;
using McpChannelVoice.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Tests.Integration.Fixtures;
using ServiceBusSettings = McpChannelServiceBus.Settings;
// Aliased because Tests.Integration has a sibling namespace named after the channel project
// (Tests.Integration.McpChannelSignalR / .McpChannelVoice), which shadows the project namespace
// from inside Tests.
using SignalRSettings = McpChannelSignalR.Settings;
using TelegramSettings = McpChannelTelegram.Settings;
using VoiceSettings = McpChannelVoice.Settings;

namespace Tests.Integration.Channels;

public class ChannelReceiveContractTests
{
    // ConfigureChannel eagerly calls ConnectionMultiplexer.Connect. abortConnect=false makes that
    // hand back a disconnected multiplexer instead of throwing, so the registration theory runs
    // without a Redis container; port 1 is refused instantly and the short timeouts keep the
    // background reconnect loop from lingering.
    private const string UnreachableRedis =
        "127.0.0.1:1,abortConnect=false,connectTimeout=100,connectRetry=1";

    // ConfigureChannel eagerly constructs a ServiceBusClient from this, which parses the
    // connection string without ever dialing out. Well-formed but unreachable — SharedAccessKey
    // just needs to be valid base64.
    private const string FakeServiceBusConnectionString =
        "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=Zm9vYmFyMTIzNDU2Nzg5MA==";

    // What McpChannelConnection("signalr") derives for itself, spelled out so a test that pins the
    // id cannot drift with the implementation it is pinning.
    private const string SignalRSubscriberId = ChannelProtocol.ChannelClientNamePrefix + "signalr";

    // One row per channel server. Later channel migrations each append exactly one row; the test
    // body is written once and never copied.
    public static TheoryData<string, Action<IMcpServerBuilder>> Servers => new()
    {
        { "signalr", b => b.WithTools<ChannelReceiveTool>() },
        { "telegram", b => b.WithTools<ChannelReceiveTool>() },
        { "servicebus", b => b.WithTools<ChannelReceiveTool>() },
        { "voice", b => b.WithTools<ChannelReceiveTool>() }
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
        },
        {
            "telegram",
            services => services.ConfigureChannel(
                new TelegramSettings.ChannelSettings { Bots = [], AllowedUsernames = [] })
        },
        {
            "servicebus",
            services => services.ConfigureChannel(
                new ServiceBusSettings.ChannelSettings
                {
                    ServiceBusConnectionString = FakeServiceBusConnectionString,
                    PromptQueueName = "prompts",
                    ResponseQueueName = "responses"
                })
        },
        {
            "voice",
            // ConfigureVoiceChannel's IConnectionMultiplexer registration is a lazy factory (unlike
            // SignalR's eager Connect), and nothing in this registration-only test resolves it, so
            // no live/fake Redis endpoint is needed — defaults are enough, exactly as ConfigModuleTests
            // already relies on for the rest of the voice DI graph.
            services => services.ConfigureVoiceChannel(new VoiceSettings.VoiceSettings())
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

        var app = await StartServerAsync(port, inbox, registerTool);
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

    [Fact]
    public async Task McpChannelConnection_SurfacesEnqueuedMessagesOnItsStream()
    {
        var port = TestPort.GetAvailable();
        var inbox = new ChannelInbox();

        var app = await StartServerAsync(port, inbox, b => b.WithTools<ChannelReceiveTool>());
        await using var connection = new McpChannelConnection("signalr");
        try
        {
            await connection.ConnectAsync($"http://localhost:{port}/mcp", CancellationToken.None);

            await Task.Delay(300);
            inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
            {
                ConversationId = "conv-1",
                Sender = "user",
                Content = "hello from the inbox",
                AgentId = "nabu"
            }));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var received = await connection.Messages.FirstAsync(cts.Token);

            received.ConversationId.ShouldBe("conv-1");
            received.Content.ShouldBe("hello from the inbox");
            received.ChannelId.ShouldBe("signalr");
        }
        finally
        {
            await connection.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task McpChannelConnection_AfterReconnect_KeepsPumping()
    {
        var port = TestPort.GetAvailable();
        var inbox = new ChannelInbox();

        var app = await StartServerAsync(port, inbox, b => b.WithTools<ChannelReceiveTool>());
        var endpoint = $"http://localhost:{port}/mcp";
        await using var connection = new McpChannelConnection("signalr");
        try
        {
            await connection.ConnectAsync(endpoint, CancellationToken.None);
            await Task.Delay(300);

            // The reconnect stops the pump and starts a fresh one on the new client; the stream
            // must go on carrying items rather than going quiet.
            await connection.ReconnectAsync(endpoint, CancellationToken.None);

            inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
            {
                ConversationId = "conv-2",
                Sender = "user",
                Content = "buffered"
            }));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var received = await connection.Messages.FirstAsync(cts.Token);

            received.Content.ShouldBe("buffered");
        }
        finally
        {
            await connection.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task McpChannelConnection_WithAStableSubscriberId_DrainsWhatBufferedWhileItWasDown()
    {
        var port = TestPort.GetAvailable();
        var inbox = new ChannelInbox();

        var app = await StartServerAsync(port, inbox, b => b.WithTools<ChannelReceiveTool>());
        var endpoint = $"http://localhost:{port}/mcp";
        var restarted = new McpChannelConnection("signalr");
        var original = new McpChannelConnection("signalr");
        try
        {
            // One agent process connects — registering its subscriber — and then goes away.
            await original.ConnectAsync(endpoint, CancellationToken.None);
            await Task.Delay(300);
            await original.DisposeAsync();

            // Barrier, not a fixture: retires the waiter the departed pump left behind, so the
            // item below cannot be handed to a poll whose caller is already gone. (That handover
            // loses the batch — see the delivery-gap note in the task report.)
            (await inbox.ReceiveAsync(SignalRSubscriberId, TimeSpan.Zero, CancellationToken.None))
                .ShouldBeEmpty();

            inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
            {
                ConversationId = "conv-2",
                Sender = "user",
                Content = "buffered"
            }));

            // A brand-new connection derives the same channel-signalr id from its channel id, so it
            // inherits the queue. An id minted per connect would orphan it and lose precisely the
            // messages that arrived while the agent was restarting.
            await restarted.ConnectAsync(endpoint, CancellationToken.None);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var received = await restarted.Messages.FirstAsync(cts.Token);

            received.Content.ShouldBe("buffered");
        }
        finally
        {
            await original.DisposeAsync();
            await restarted.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task McpChannelConnection_WhenServerIsDown_DisposesWithoutHanging()
    {
        // No server listening on this port at all.
        var port = TestPort.GetAvailable();
        await using var connection = new McpChannelConnection("signalr");

        // ConnectAsync throws, so no pump was ever started; dispose must not wait on one.
        await Should.ThrowAsync<Exception>(
            () => connection.ConnectAsync($"http://localhost:{port}/mcp", CancellationToken.None));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await connection.DisposeAsync().AsTask().WaitAsync(cts.Token);
    }

    [Fact]
    public async Task McpChannelConnection_ConnectingTwice_LeavesOnlyOnePumpPolling()
    {
        var port = TestPort.GetAvailable();
        var inbox = new ChannelInbox();
        var posts = 0;

        var app = await StartServerAsync(
            port,
            inbox,
            b => b.WithTools<ChannelReceiveTool>(),
            web => web.Use(async (context, next) =>
            {
                if (HttpMethods.IsPost(context.Request.Method))
                {
                    Interlocked.Increment(ref posts);
                }

                await next(context);
            }));

        var endpoint = $"http://localhost:{port}/mcp";
        await using var connection = new McpChannelConnection("signalr");
        try
        {
            await connection.ConnectAsync(endpoint, CancellationToken.None);
            await Task.Delay(300);

            // A second connect must retire the first pump. Two pumps share one subscriberId and
            // displace each other's waiter, which ChannelInbox retires with an *instant* empty
            // batch — indistinguishable from a timeout, so both re-poll at once and peg a core.
            await connection.ConnectAsync(endpoint, CancellationToken.None);
            await Task.Delay(300);

            Interlocked.Exchange(ref posts, 0);
            await Task.Delay(TimeSpan.FromSeconds(1.5));

            // One pump holding one long poll open: the server should see nothing else.
            Volatile.Read(ref posts).ShouldBeLessThanOrEqualTo(3);
        }
        finally
        {
            await connection.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task AHeldLongPoll_DoesNotBlockOtherToolCallsOnTheSameClient()
    {
        var port = TestPort.GetAvailable();
        var inbox = new ChannelInbox();

        var app = await StartServerAsync(
            port,
            inbox,
            b => b.WithTools<ChannelReceiveTool>().WithTools<ImmediateTool>());
        try
        {
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"http://localhost:{port}/mcp")
                }));

            var held = Poll(client, SignalRSubscriberId, maxWaitMs: 10_000);
            await Task.Delay(200);

            var startedAt = Stopwatch.GetTimestamp();
            foreach (var _ in Enumerable.Range(0, 5))
            {
                var pong = await client.CallToolAsync("ping");
                pong.Content.OfType<TextContentBlock>().First().Text.ShouldBe("pong");
            }

            var elapsed = Stopwatch.GetElapsedTime(startedAt);

            // The pump holds a 30 s channel_receive open on the same McpClient that send_reply
            // streams over. If the SDK ever serialized concurrent tools/call, every reply chunk
            // would queue behind that poll — invisible to every other test, catastrophic for voice.
            held.IsCompleted.ShouldBeFalse("the poll must still be held while the pings run");
            elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));

            inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
            {
                ConversationId = "conv-3",
                Sender = "user",
                Content = "release"
            }));

            (await held).Items.Count.ShouldBe(1);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<WebApplication> StartServerAsync(
        int port,
        ChannelInbox inbox,
        Action<IMcpServerBuilder> registerTool,
        Action<WebApplication>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.AddSingleton(inbox);
        registerTool(builder.Services.AddMcpServer().WithHttpTransport());

        var app = builder.Build();
        configure?.Invoke(app);
        app.MapMcp("/mcp");
        await app.StartAsync();
        return app;
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

// A second tool for the concurrency pin: something that answers instantly while a long poll is
// held open on the same client.
[McpServerToolType]
public sealed class ImmediateTool
{
    [McpServerTool(Name = "ping")]
    [Description("Test tool that answers immediately.")]
    public static string McpRun() => "pong";
}