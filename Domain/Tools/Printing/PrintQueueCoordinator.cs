using Domain.Contracts;

namespace Domain.Tools.Printing;

// Submits documents once their writes go quiet (the fs_blob_write protocol gives no EOF),
// and prunes finished jobs so the queue only shows active work.
public sealed class PrintQueueCoordinator(
    IPrintSpool spool,
    IPrinterClient printer,
    TimeProvider clock,
    TimeSpan submitDebounce)
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

        var activeIds = (await printer.GetActiveJobsAsync(ct)).Select(j => j.JobId).ToHashSet();
        var finished = submitted.Where(e => !activeIds.Contains(e.JobId!.Value));

        foreach (var entry in finished)
        {
            await spool.RemoveAsync(entry.FileName, ct);
        }
    }
}