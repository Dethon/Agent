using Domain.Contracts;
using Domain.DTOs;
using McpServerLibrary.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServerLibrary.Services;

public sealed class DownloadCompletionWatcher(
    IDownloadRoutingStore store,
    IDownloadClient client,
    IDownloadNotificationEmitter emitter,
    McpSettings settings,
    ILogger<DownloadCompletionWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.CompletionPollSeconds));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error sweeping downloads for completion");
            }

            try
            { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        if (!emitter.HasActiveSessions)
        {
            return;
        }

        var entries = await store.ListAsync(ct);
        if (entries.Count == 0)
        {
            return;
        }

        var items = (await client.GetDownloadItems(ct)).ToDictionary(i => i.Id);
        foreach (var entry in entries)
        {
            if (!items.TryGetValue(entry.DownloadId, out var item))
            {
                await store.RemoveAsync(entry.DownloadId, ct);
                continue;
            }

            if (item.State is not DownloadState.Completed)
            {
                continue;
            }

            if (!await emitter.EmitAsync(DownloadCompletionPlanner.BuildPayload(entry), ct))
            {
                logger.LogWarning(
                    "No active session received completion for download {DownloadId}; will retry", entry.DownloadId);
                continue;
            }

            await store.RemoveAsync(entry.DownloadId, ct);
            logger.LogInformation(
                "Emitted completion for download {DownloadId} ('{Title}')", entry.DownloadId, entry.Title);
        }
    }
}