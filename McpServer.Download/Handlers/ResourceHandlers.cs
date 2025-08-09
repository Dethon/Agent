using Domain.Contracts;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServer.Download.Handlers;

public static class ResourceHandlers
{
    public static ValueTask<EmptyResult> SubscribeToResource(
        RequestContext<SubscribeRequestParams> context, CancellationToken cancellationToken)
    {
        var sessionId = context.Server.SessionId ?? "";
        var uri = context.Params?.Uri;
        if (context.Services is null || string.IsNullOrEmpty(uri))
        {
            throw new InvalidOperationException("Service injection fault or URI is not available.");
        }
        var stateManager = context.Services.GetRequiredService<IStateManager>();
        stateManager.SubscribeResource(sessionId, uri, context.Server);
        return ValueTask.FromResult(new EmptyResult());
    }
    
    public static ValueTask<EmptyResult> UnsubscribeToResource(
        RequestContext<UnsubscribeRequestParams> context, CancellationToken _)
    {
        var sessionId = context.Server.SessionId ?? "";
        var uri = context.Params?.Uri;
        var stateManager = context.Services?.GetRequiredService<IStateManager>();
        if (stateManager is null || string.IsNullOrEmpty(uri))
        {
            throw new InvalidOperationException("State manager or URI is not available.");
        }

        stateManager.UnsubscribeResource(sessionId, uri);
        return new ValueTask<EmptyResult>();
    }
}