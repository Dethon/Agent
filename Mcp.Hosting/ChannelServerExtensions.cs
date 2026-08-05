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
    // noOutboundSurface is opt-in rather than defaulted: a server that cannot carry a reply back to a
    // person says so, and gets the two no-op protocol tools. Defaulting it the other way would let a
    // real channel that forgot its reply tool silently drop every reply, and at registration time
    // nothing can tell "deliberately absent" from "forgotten".
    public static IMcpServerBuilder AddChannelServer(
        this IMcpServerBuilder builder,
        DeliveryPolicy policy,
        string? subscriberId = null,
        Func<Exception, CallToolResult>? errorResult = null,
        bool noOutboundSurface = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        DeliveryPolicyRules.ValidateSubscriberId(policy, subscriberId);

        // The call-tool filter guards itself and the first ask wins, but the inbox and the emitter
        // are plain singletons, so a second call would silently replace the emitter and with it the
        // declared delivery policy. A server has one inbox and one policy; asking twice is a bug in
        // the server, and it says so here rather than surfacing later as a duplicate tool name.
        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(ChannelNotificationEmitter)))
        {
            throw new InvalidOperationException(
                "AddChannelServer has already been called on this server. A channel server has one "
                + "inbox and one delivery policy; a second call would replace the first policy.");
        }

        builder.Services
            .AddSingleton<ChannelInbox>()
            .AddSingleton(sp => new ChannelNotificationEmitter(
                sp.GetRequiredService<ChannelInbox>(), policy, subscriberId));

        builder = builder.WithTools<McpChannelReceiveTool>();

        return (noOutboundSurface ? builder.WithTools<NoOutboundSurfaceTools>() : builder)
            .AddCallToolErrorFilter(errorResult);
    }
}