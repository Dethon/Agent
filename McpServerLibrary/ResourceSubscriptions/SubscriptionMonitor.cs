using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpServerLibrary.ResourceSubscriptions;

public class SubscriptionMonitor(
    ITrackedDownloadsManager trackedDownloadsManager,
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

    private static bool TryParseDownloadIdFromUri(string uri, out int id)
    {
        id = 0;
        if (!uri.StartsWith("download://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = uri["download://".Length..].TrimEnd('/');
        return int.TryParse(remainder, out id);
    }

    private async Task DownloadMonitoring(
        string sessionId,
        string uri,
        McpServer server,
        CancellationToken cancellationToken)
    {
        // Most subscriptions are to a concrete resource like download://123/.
        // Only check that specific download; otherwise, concurrent completions can be reported on the wrong URI.
        if (!uri.Contains("{id}", StringComparison.OrdinalIgnoreCase) &&
            TryParseDownloadIdFromUri(uri, out var subscribedId))
        {
            await MonitorSpecificDownload(sessionId, uri, subscribedId, server, cancellationToken);
            return;
        }

        await MonitorAllDownloads(sessionId, uri, server, cancellationToken);
    }

    private async Task MonitorSpecificDownload(
        string sessionId,
        string uri,
        int downloadId,
        McpServer server,
        CancellationToken cancellationToken)
    {
        // Only emit a completion update once. Clients stay subscribed, so without this guard we'd re-notify forever.
        var tracked = trackedDownloadsManager.Get(sessionId);
        if (tracked is null || !tracked.Contains(downloadId))
        {
            return;
        }

        var downloadItem = await downloadClient.GetDownloadItem(downloadId, cancellationToken);

        if (downloadItem is not null && downloadItem.State is not DownloadState.Completed)
        {
            return;
        }

        trackedDownloadsManager.Remove(sessionId, downloadId);
        await server.SendNotificationAsync(
            "notifications/resources/updated",
            new { Uri = uri },
            cancellationToken: cancellationToken);

        if (downloadItem is null)
        {
            await server.SendNotificationAsync(
                "notifications/resources/list_changed",
                cancellationToken: cancellationToken);
        }
    }

    private async Task MonitorAllDownloads(
        string sessionId,
        string uriTemplate,
        McpServer server,
        CancellationToken cancellationToken)
    {
        var downloadIds = trackedDownloadsManager.Get(sessionId) ?? [];

        //TODO: Check all downloads in a single call
        var downloadTasks = downloadIds
            .Select(async x => (
                DownloadId: x,
                DownloadItem: await downloadClient.GetDownloadItem(x, cancellationToken)
            ));
        var downloads = await Task.WhenAll(downloadTasks);
        var filteredDownloads = downloads
            .Where(x => x.DownloadItem == null || x.DownloadItem.State is DownloadState.Completed)
            .ToArray();

        foreach (var (id, _) in filteredDownloads)
        {
            trackedDownloadsManager.Remove(sessionId, id);
            await server.SendNotificationAsync(
                "notifications/resources/updated",
                new { Uri = uriTemplate.Replace("{id}", $"{id}") },
                cancellationToken: cancellationToken);
        }

        if (filteredDownloads.Any(x => x.DownloadItem == null))
        {
            await server.SendNotificationAsync(
                "notifications/resources/list_changed",
                cancellationToken: cancellationToken);
        }
    }
}