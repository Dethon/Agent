using Domain.Channels;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.Hosting;

// What "being a channel server" means, as one call. It extends the MCP server builder rather than
// the service collection because the transport tool and the call-tool filter have to join the
// builder chain; the inbox and the emitter go on the builder's services from there. A new transport
// supplies only the thing that is genuinely transport-specific — how it sends a reply.
public static class ChannelServerExtensions
{
    public static IMcpServerBuilder AddChannelServer(
        this IMcpServerBuilder builder,
        DeliveryPolicy policy,
        string? subscriberId = null,
        Func<Exception, CallToolResult>? errorResult = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        DeliveryPolicyRules.ValidateSubscriberId(policy, subscriberId);

        builder.Services
            .AddSingleton<ChannelInbox>()
            .AddSingleton(sp => new ChannelNotificationEmitter(
                sp.GetRequiredService<ChannelInbox>(), policy, subscriberId));

        return builder
            .WithTools<McpChannelReceiveTool>()
            .AddCallToolErrorFilter(errorResult);
    }
}