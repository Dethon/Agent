using Domain.Contracts;
using Domain.DTOs;
using Mcp.Hosting;
using McpServerLibrary.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServerLibrary.Services;

public sealed class DownloadCompletionWatcher(
    IDownloadRoutingStore store,
    IDownloadClient client,
    ChannelNotificationEmitter emitter,
    McpSettings settings,
    ILogger<DownloadCompletionWatcher> logger) : BackgroundService
{
    private bool _warnedUndelivered;

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
                WarnUndeliveredOncePerOutage(entry.DownloadId);
                continue;
            }

            NoteDeliveryResumed();
            await store.RemoveAsync(entry.DownloadId, ct);
            logger.LogInformation(
                "Emitted completion for download {DownloadId} ('{Title}')", entry.DownloadId, entry.Title);
        }
    }

    // One warning per outage rather than one per pending download per tick: an overnight
    // disconnection with a single completed download used to produce thousands of identical
    // lines. The retry semantics are untouched — the routing entry stays either way.
    private void WarnUndeliveredOncePerOutage(int downloadId)
    {
        if (_warnedUndelivered)
        {
            return;
        }

        _warnedUndelivered = true;
        logger.LogWarning(
            "No active session received completion for download {DownloadId}; retrying pending completions and muting this warning until delivery resumes",
            downloadId);
    }

    private void NoteDeliveryResumed()
    {
        if (_warnedUndelivered)
        {
            _warnedUndelivered = false;
            logger.LogInformation("Completion delivery resumed; an active session is receiving alerts again");
        }
    }
}