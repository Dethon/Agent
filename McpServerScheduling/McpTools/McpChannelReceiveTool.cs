using System.ComponentModel;
using Domain.Channels;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public sealed class McpChannelReceiveTool(ChannelInbox inbox) : Domain.Tools.Channels.ChannelReceiveTool(inbox)
{
    [McpServerTool(Name = ChannelProtocol.ReceiveTool)]
    [Description(Description)]
    public Task<string> McpRun(
        [Description(SubscriberIdDescription)] string subscriberId,
        [Description(MaxWaitMsDescription)] int maxWaitMs,
        CancellationToken cancellationToken)
        => Run(subscriberId, maxWaitMs, cancellationToken);
}