using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerDownload.ResourceSubscriptions;

public static class SubscriptionHandlers
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

        var subscriptionTracker = context.Services.GetRequiredService<SubscriptionTracker>();
        subscriptionTracker.Add(sessionId, uri, context.Server);
        return ValueTask.FromResult(new EmptyResult());
    }
    
    public static ValueTask<EmptyResult> UnsubscribeToResource(
        RequestContext<UnsubscribeRequestParams> context, CancellationToken _)
    {
        var sessionId = context.Server.SessionId ?? "";
        var uri = context.Params?.Uri;
        var subscriptionTracker = context.Services?.GetRequiredService<SubscriptionTracker>();
        if (subscriptionTracker is null || string.IsNullOrEmpty(uri))
        {
            throw new InvalidOperationException("State manager or URI is not available.");
        }

        subscriptionTracker.Remove(sessionId, uri);
        return ValueTask.FromResult(new EmptyResult());
    }
}