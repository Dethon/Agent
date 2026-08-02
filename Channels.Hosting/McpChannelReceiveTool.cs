using System.ComponentModel;
using Domain.Channels;
using Domain.DTOs.Channel;
using Domain.Tools.Channels;
using ModelContextProtocol.Server;

namespace Channels.Hosting;

// The one transport half of channel_receive. Registration is always explicit (WithTools<T>), never
// assembly scanning, so a tool type living in this assembly registers into any channel server.
[McpServerToolType]
public sealed class McpChannelReceiveTool(ChannelInbox inbox) : ChannelReceiveTool(inbox)
{
    [McpServerTool(Name = ChannelProtocol.ReceiveTool)]
    [Description(Description)]
    public Task<string> McpRun(
        [Description(SubscriberIdDescription)] string subscriberId,
        [Description(MaxWaitMsDescription)] int maxWaitMs,
        CancellationToken cancellationToken)
        => Run(subscriberId, maxWaitMs, cancellationToken);
}
