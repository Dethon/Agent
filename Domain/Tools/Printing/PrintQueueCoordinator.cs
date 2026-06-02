using Domain.Contracts;

namespace Domain.Tools.Printing;

// Submits documents once their writes go quiet (the fs_blob_write protocol gives no EOF),
// and prunes finished jobs so the queue only shows active work.
public sealed class PrintQueueCoordinator(
    IPrintSpool spool,
    IPrinterClient printer,
    TimeProvider clock,
    TimeSpan submitDebounce,
    TimeSpan reconcileGrace)
{
    public async Task TickAsync(CancellationToken ct)
    {
        await SubmitDueAsync(ct);
        await ReconcileAsync(ct);
    }

    public async Task SubmitDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var entries = await spool.ListAsync(ct);
        var due = entries.Where(e => !e.IsSubmitted && now - e.LastWriteAt >= submitDebounce);

        foreach (var entry in due)
        {
            var bytes = await spool.ReadAllBytesAsync(entry.FileName, ct);
            if (bytes is null)
            {
                continue;
            }

            var handle = await printer.SubmitAsync(entry.FileName, entry.ContentType, bytes, ct);
            await spool.MarkSubmittedAsync(entry.FileName, handle.JobId, clock.GetUtcNow(), ct);
        }
    }

    public async Task ReconcileAsync(CancellationToken ct)
    {
        var entries = await spool.ListAsync(ct);
        var submitted = entries.Where(e => e.IsSubmitted).ToList();
        if (submitted.Count == 0)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var activeIds = (await printer.GetActiveJobsAsync(ct)).Select(j => j.JobId).ToHashSet();

        foreach (var entry in submitted)
        {
            if (activeIds.Contains(entry.JobId!.Value))
            {
                // Live on the printer; clear any earlier "missing" mark so a brief blip doesn't count.
                if (entry.MissingSince is not null)
                {
                    await spool.SetMissingSinceAsync(entry.FileName, null, ct);
                }

                continue;
            }

            // Absent from the active set. A just-submitted job the printer hasn't registered yet, and a
            // transient empty/partial Get-Jobs response, both look like this — so don't prune on first
            // sight. Record when it went missing and only prune once it has stayed gone past the grace
            // window (debounced absence), which is the reliable signal that it actually finished.
            var missingSince = entry.MissingSince ?? now;
            if (entry.MissingSince is null)
            {
                await spool.SetMissingSinceAsync(entry.FileName, missingSince, ct);
            }

            if (now - missingSince >= reconcileGrace)
            {
                await spool.RemoveAsync(entry.FileName, ct);
            }
        }
    }
}