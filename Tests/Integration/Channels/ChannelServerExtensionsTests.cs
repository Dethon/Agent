using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Channels.Hosting;
using Domain.Channels;
using Domain.DTOs.Channel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Channels;

// The registration seam. One call wires the inbox, the transport tool, the error filter and the
// emitter, so a new transport supplies only its reply-sending logic. These assertions are about
// the shared call itself; each real server's use of it is pinned by ChannelReceiveContractTests.
public class ChannelServerExtensionsTests
{
    [Fact]
    public void AddChannelServer_RegistersTheInboxTheEmitterAndTheTransportTool()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().AddChannelServer(DeliveryPolicy.Broadcast);

        using var provider = services.BuildServiceProvider();

        provider.GetService<ChannelInbox>().ShouldNotBeNull();
        provider.GetRequiredService<ChannelNotificationEmitter>().Policy.ShouldBe(DeliveryPolicy.Broadcast);
        provider.GetServices<McpServerTool>()
            .Select(tool => tool.ProtocolTool.Name)
            .ShouldContain(ChannelProtocol.ReceiveTool);
    }

    [Fact]
    public void AddChannelServer_BufferAlwaysWithoutASubscriberId_ThrowsAtRegistration() =>
        Should.Throw<ArgumentException>(() =>
            new ServiceCollection().AddMcpServer().AddChannelServer(DeliveryPolicy.BufferAlways));

    [Fact]
    public void AddChannelServer_BroadcastWithASubscriberId_ThrowsAtRegistration() =>
        Should.Throw<ArgumentException>(() =>
            new ServiceCollection().AddMcpServer().AddChannelServer(DeliveryPolicy.Broadcast, "channel-x"));

    // The rule the six copies of this filter all had to state for themselves: a long poll ends in
    // cancellation whenever the agent hangs up or the server shuts down, and mapping that to an
    // error result would hand the agent's pump something to retry on.
    //
    // Asserted through a marked error mapper rather than by expecting a throw: the SDK's own outer
    // handler catches whatever the filter rethrows and answers with a generic message of its own,
    // so "did this exception reach the filter's error path" is the question that can be observed
    // over the wire — and it is the question the rule is about.
    [Fact]
    public async Task CallToolFilter_ACancelledCall_DoesNotBecomeTheFiltersErrorResult()
    {
        await using var server = await StartAsync(Marked);

        var cancelled = await server.Client.CallToolAsync("cancels");
        var failed = await server.Client.CallToolAsync("throws");

        Text(cancelled).ShouldNotContain(Marker);
        Text(failed).ShouldContain(Marker);
    }

    private const string Marker = "mapped-by-the-channel-filter";

    private static CallToolResult Marked(Exception ex) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"{Marker}: {ex.Message}" }]
    };

    private static string Text(CallToolResult result) =>
        string.Join("|", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    [Fact]
    public async Task CallToolFilter_AnyOtherException_BecomesAnErrorResult()
    {
        await using var server = await StartAsync();

        var result = await server.Client.CallToolAsync("throws");

        result.IsError.ShouldBe(true);
        result.Content.OfType<TextContentBlock>().First().Text.ShouldContain("boom");
    }

    // The two dual-role servers answer with their own envelope (ToolResponse.Create) rather than a
    // bare message, and that shape lives in Infrastructure, which this project must not reference.
    // So the shape is the caller's to supply; the rule about which exceptions reach it is not.
    [Fact]
    public async Task CallToolFilter_AcceptsACallersOwnErrorShape()
    {
        await using var server = await StartAsync(ex => new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"{{\"ok\":false,\"message\":\"{ex.Message}\"}}" }]
        });

        var result = await server.Client.CallToolAsync("throws");

        result.IsError.ShouldBe(true);
        result.Content.OfType<TextContentBlock>().First().Text.ShouldContain("\"ok\":false");
    }

    [Fact]
    public async Task ChannelReceive_DeliversWhatTheEmitterPutOnTheInbox()
    {
        await using var server = await StartAsync();
        var emitter = server.App.Services.GetRequiredService<ChannelNotificationEmitter>();

        await Poll(server.Client, maxWaitMs: 0);
        await emitter.EmitAsync(new ChannelMessageNotification
        {
            ConversationId = "conv-1",
            Sender = "user",
            Content = "hello"
        });

        var batch = await Poll(server.Client, maxWaitMs: 0);

        batch.Items.Count.ShouldBe(1);
        batch.Items[0].Message!.Content.ShouldBe("hello");
    }

    private const string SubscriberId = ChannelProtocol.ChannelClientNamePrefix + "test";

    private static async Task<ChannelReceiveResult> Poll(McpClient client, int maxWaitMs)
    {
        var call = await client.CallToolAsync(
            ChannelProtocol.ReceiveTool,
            new Dictionary<string, object?> { ["subscriberId"] = SubscriberId, ["maxWaitMs"] = maxWaitMs });

        var text = call.Content.OfType<TextContentBlock>().First().Text;
        return JsonSerializer.Deserialize<ChannelReceiveResult>(text, ChannelProtocol.SerializerOptions)!;
    }

    private static async Task<RunningServer> StartAsync(Func<Exception, CallToolResult>? errorResult = null)
    {
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<FailingTools>()
            .AddChannelServer(DeliveryPolicy.Broadcast, errorResult: errorResult);

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri($"http://localhost:{port}/mcp")
            }));

        return new RunningServer(app, client);
    }

    private sealed record RunningServer(WebApplication App, McpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}

[McpServerToolType]
public sealed class FailingTools
{
    [McpServerTool(Name = "throws")]
    [Description("Test tool that always throws.")]
    public static string Throws() => throw new InvalidOperationException("boom");

    [McpServerTool(Name = "cancels")]
    [Description("Test tool that always cancels, like an aborted long poll.")]
    public static string Cancels() => throw new OperationCanceledException();
}