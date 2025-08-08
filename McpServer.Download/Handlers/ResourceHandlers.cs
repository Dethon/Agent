using Domain.Contracts;
using Domain.DTOs;
using Domain.Monitor;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServer.Download.Handlers;

public static class ResourceHandlers
{
    public static async ValueTask<EmptyResult> SubscribeToResource(
        RequestContext<SubscribeRequestParams> context, CancellationToken cancellationToken)
    {
        var sessionId = context.Server.SessionId ?? "";
        var uri = context.Params?.Uri;
        if (context.Services is null || string.IsNullOrEmpty(uri))
        {
            throw new InvalidOperationException("Service injection fault or URI is not available.");
        }
        var stateManager = context.Services.GetRequiredService<IStateManager>();
        var taskQueue = context.Services.GetRequiredService<TaskQueue>();
        
        stateManager.SubscribeResource(sessionId, uri);
        var monitor = GetResourceMonitor(sessionId, uri, context.Server, stateManager, context.Services);
        await taskQueue.QueueTask(monitor);
        
        return new EmptyResult();
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
    
    private static Func<CancellationToken, Task> GetResourceMonitor(
        string sessionId, 
        string uri, 
        IMcpServer server, 
        IStateManager stateManager, 
        IServiceProvider services)
    {
        if(uri.StartsWith("download://", StringComparison.OrdinalIgnoreCase))
        {
            var downloadClient = services.GetRequiredService<IDownloadClient>();
            return ct => DownloadMonitoring(sessionId, uri, server, stateManager, downloadClient, ct);
        }
        throw new NotImplementedException();
    }

    private static async Task DownloadMonitoring(
        string sessionId, 
        string uri, 
        IMcpServer server, 
        IStateManager stateManager,
        IDownloadClient downloadClient,
        CancellationToken cancellationToken)
    {
        while (stateManager.GetSubscribedResources(sessionId)?.Contains(uri) ?? false)
        {
            var downloadIds = stateManager.GetTrackedDownloads(sessionId) ?? [];
            
            //TODO: Chack all downloads in a single call
            var downloadTasks = downloadIds
                .Select(async x => (
                    DownloadId: x,
                    DownloadItem: await downloadClient.GetDownloadItem(x, 3, 500, cancellationToken)
                ));
            var downloads = await Task.WhenAll(downloadTasks);
            var filteredDownloads = downloads
                .Where(x => x.DownloadItem == null || x.DownloadItem.State is DownloadState.Completed);
            
            foreach (var (id, _) in filteredDownloads)
            {
                stateManager.UntrackDownload(sessionId, id);
                await server.SendNotificationAsync("notifications/resources/updated",
                    new
                    {
                        Uri = uri.Replace("{id}", $"{id}")
                    }, cancellationToken: cancellationToken);
            }
            
            await Task.Delay(1000, cancellationToken);
        }
    }
}