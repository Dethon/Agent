using System.Net;
using System.Text.Json;
using Domain.Channels;
using Domain.DTOs.Channel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;
// Aliased because Tests.Integration has sibling namespaces named after the channel projects
// (Tests.Integration.McpChannelSignalR), which shadow the project namespaces from inside Tests.
using SignalRChannel = McpChannelSignalR.McpTools;

namespace Tests.Integration.Channels;

public class ChannelReceiveContractTests
{
    // One row per channel server. Later channel migrations each append exactly one row; the test
    // body is written once and never copied.
    public static TheoryData<string, string, Action<IMcpServerBuilder>> Servers => new()
    {
        { "signalr", "channel-signalr", b => b.WithTools<SignalRChannel.ChannelReceiveTool>() }
    };

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task EnqueuedMessage_IsDeliveredToAPollingClient(
        string channelId, string subscriberId, Action<IMcpServerBuilder> registerTool)
    {
        _ = channelId;
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