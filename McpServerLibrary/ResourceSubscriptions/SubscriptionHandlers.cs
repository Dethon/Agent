using Domain.Contracts;
using McpServerLibrary.Extensions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.ResourceSubscriptions;

public static class SubscriptionHandlers
{
    public static ValueTask<EmptyResult> SubscribeToResource(
        RequestContext<SubscribeRequestParams> context, CancellationToken cancellationToken)
    {
        var sessionId = context.Server.StateKey;
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
        var sessionId = context.Server.StateKey;
        var uri = context.Params?.Uri;
        var subscriptionTracker = context.Services?.GetRequiredService<SubscriptionTracker>();
        if (subscriptionTracker is null || string.IsNullOrEmpty(uri))
        {
            throw new InvalidOperationException("State manager or URI is not available.");
        }

        subscriptionTracker.Remove(sessionId, uri);
        return ValueTask.FromResult(new EmptyResult());
    }

    public static ValueTask<ListResourcesResult> ListResources(
        RequestContext<ListResourcesRequestParams> context, CancellationToken _)
    {
        if (context.Services is null)
        {
            throw new InvalidOperationException("Services are not available.");
        }

        var trackedDownloadsManager = context.Services.GetRequiredService<ITrackedDownloadsManager>();
        var stateKey = context.Server.StateKey;

        var downloadIds = trackedDownloadsManager.Get(stateKey) ?? [];
        var resources = downloadIds.Select(id => new Resource
        {
            Uri = $"download://{id}/",
            Name = $"Download {id}",
            Description = $"Status of download with ID {id}",
            MimeType = "text/plain"
        }).ToList();

        return ValueTask.FromResult(new ListResourcesResult { Resources = resources });
    }
}