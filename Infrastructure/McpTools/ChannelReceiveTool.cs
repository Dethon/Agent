using System.ComponentModel;
using System.Text.Json;
using Domain.Channels;
using Domain.DTOs.Channel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Infrastructure.McpTools;

[McpServerToolType]
public sealed class ChannelReceiveTool
{
    [McpServerTool(Name = ChannelProtocol.ReceiveTool)]
    [Description("Internal channel transport. Long-polls for inbound channel items.")]
    public static async Task<string> McpRun(
        [Description("Stable subscriber id, e.g. channel-signalr")] string subscriberId,
        [Description("How long to hold the request open, in milliseconds")] int maxWaitMs,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var inbox = services.GetRequiredService<ChannelInbox>();
        var items = await inbox.ReceiveAsync(
            subscriberId, TimeSpan.FromMilliseconds(maxWaitMs), cancellationToken);

        return JsonSerializer.Serialize(
            new ChannelReceiveResult { Items = items }, ChannelProtocol.SerializerOptions);
    }
}