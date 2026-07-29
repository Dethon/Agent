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

        // Clamped, not just conventionally short today: ChannelProtocol.LiveSubscriberFreshness
        // gives a subscriber headroom for one fully held poll plus one retry backoff on the
        // assumption that no single poll holds the request open longer than DefaultReceiveWaitMs
        // (a subscriber is stamped
        // when its poll *starts* — see ChannelInbox.Subscriber's own doc comment). An unclamped
        // maxWaitMs from a future/misbehaving caller could park a genuinely live subscriber past
        // the freshness window, making HasLiveSubscriber read it as dead — worse than the bug the
        // freshness check exists to fix.
        var clampedWaitMs = Math.Clamp(maxWaitMs, 0, ChannelProtocol.DefaultReceiveWaitMs);
        var items = await inbox.ReceiveAsync(
            subscriberId, TimeSpan.FromMilliseconds(clampedWaitMs), cancellationToken);

        return JsonSerializer.Serialize(
            new ChannelReceiveResult { Items = items }, ChannelProtocol.SerializerOptions);
    }
}