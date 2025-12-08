using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpServerLibrary.ResourceSubscriptions;

public class SubscriptionMonitor(
    IStateManager stateManager,
    SubscriptionTracker subscriptionsTracker,
    IDownloadClient downloadClient,
    ILogger<SubscriptionMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var allSubscriptions = subscriptionsTracker.Get();
            foreach (var (sessionId, value) in allSubscriptions)
            {
                foreach (var (uri, server) in value)
                {
                    try
                    {
                        var resourceCheck = GetResourceCheck(sessionId, uri, server);
                        await resourceCheck(cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        if (logger.IsEnabled(LogLevel.Error))
                        {
                            logger.LogError(
                                ex,
                                "Error when monitoring resources for URI {Uri} and Session {SessionId}", uri,
                                sessionId);
                        }
                    }
                }
            }

            await Task.Delay(5000, cancellationToken);
        }
    }

    private Func<CancellationToken, Task> GetResourceCheck(string sessionId, string uri, McpServer server)
    {
        if (uri.StartsWith("download://", StringComparison.OrdinalIgnoreCase))
        {
            return ct => DownloadMonitoring(sessionId, uri, server, ct);
        }

        throw new NotImplementedException();
    }

    private async Task DownloadMonitoring(
        string sessionId,
        string uri,
        McpServer server,
        CancellationToken cancellationToken)
    {
        var downloadIds = stateManager.TrackedDownloads.Get(sessionId) ?? [];

        //TODO: Check all downloads in a single call
        var downloadTasks = downloadIds
            .Select(async x => (
                DownloadId: x,
                DownloadItem: await downloadClient.GetDownloadItem(x, cancellationToken)
            ));
        var downloads = await Task.WhenAll(downloadTasks);
        var filteredDownloads = downloads
            .Where(x => x.DownloadItem == null || x.DownloadItem.State is DownloadState.Completed);

        foreach (var (id, _) in filteredDownloads)
        {
            await server.SendNotificationAsync("notifications/resources/updated",
                new
                {
                    Uri = uri.Replace("{id}", $"{id}")
                }, cancellationToken: cancellationToken);
        }
    }
}