namespace Domain.Tools.Printing;

// Serializes compound print-queue operations that span several spool + printer calls — the
// background submit/reconcile cycle (PrintQueueCoordinator) versus foreground create/edit/delete/
// copy (PrinterQueueFileSystem). PrintSpool's per-call lock makes each individual call atomic but
// can't make these multi-step sequences mutually exclusive, so both sides take this shared gate to
// stop a background submit from racing a foreground cancel (e.g. printing a just-cancelled doc).
public sealed class PrintQueueGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Releaser(_gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}