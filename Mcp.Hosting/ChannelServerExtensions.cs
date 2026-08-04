using Domain.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            .WithRequestFilters(filters => filters.AddCallToolFilter(
                next => async (context, cancellationToken) =>
                {
                    try
                    {
                        return await next(context, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // channel_receive's long poll ends in cancellation whenever the agent hangs
                        // up or the server shuts down. Mapping that to IsError would hand the pump
                        // an error result to retry on; let it propagate as the abort it is.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        context.Services?.GetService<ILoggerFactory>()
                            ?.CreateLogger(typeof(ChannelServerExtensions))
                            .LogError(ex, "Error in {ToolName} tool", context.Params?.Name);
                        return errorResult?.Invoke(ex) ?? new CallToolResult
                        {
                            IsError = true,
                            Content = [new TextContentBlock { Text = ex.Message }]
                        };
                    }
                }));
    }
}