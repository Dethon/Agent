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

    // Liveness is only ever the return value of emitting (see CLAUDE.md) — never a separate
    // property to query. This is not that: it is the loop remembering the outcome of its own last
    // attempt, to decide how soon to try again. Six missed ticks between qBittorrent and
    // routing-store queries while nobody is listening, rather than one.
    internal const int IdleBackoffMultiplier = 6;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.CompletionPollSeconds));
        while (!ct.IsCancellationRequested)
        {
            var delivered = true;
            try
            {
                delivered = await SweepAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error sweeping downloads for completion");
            }

            try
            { await Task.Delay(NextDelay(interval, delivered), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal static TimeSpan NextDelay(TimeSpan interval, bool delivered) =>
        delivered ? interval : interval * IdleBackoffMultiplier;

    // Returns whether the sweep needs no back-off: nothing was pending, or every completed
    // download it tried to hand off was actually delivered. False means at least one delivery was
    // refused for want of a listener, which is the loop's cue to slow down.
    internal async Task<bool> SweepAsync(CancellationToken ct)
    {
        var delivered = await SweepEntriesAsync(ct);
        if (delivered)
        {
            NoteDeliveryResumed();
        }

        return delivered;
    }

    private async Task<bool> SweepEntriesAsync(CancellationToken ct)
    {
        var entries = await store.ListAsync(ct);
        if (entries.Count == 0)
        {
            return true;
        }

        var items = (await client.GetDownloadItems(ct)).ToDictionary(i => i.Id);
        var delivered = true;
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
                delivered = false;
                continue;
            }

            await store.RemoveAsync(entry.DownloadId, ct);
            logger.LogInformation(
                "Emitted completion for download {DownloadId} ('{Title}')", entry.DownloadId, entry.Title);
        }

        return delivered;
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

    // Cleared on any sweep that left nothing waiting on a listener, not only on a successful emit:
    // the completion an outage held up is often gone by the time the agent returns, so waiting for
    // a delivery to unmute would leave the next outage silent.
    private void NoteDeliveryResumed()
    {
        if (_warnedUndelivered)
        {
            _warnedUndelivered = false;
            logger.LogInformation("Completion delivery resumed; no alert is waiting on a listener");
        }
    }
}