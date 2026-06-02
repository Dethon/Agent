using Domain.Contracts;
using Domain.DTOs.Printing;

namespace Tests.Unit.Domain.Printing;

public sealed class FakePrinterClient : IPrinterClient
{
    private readonly Dictionary<int, PrintJobStatus> _active = new();
    private int _nextId;

    public List<(string JobName, string ContentType, byte[] Document)> Submissions { get; } = new();
    public List<int> Canceled { get; } = new();

    public Task<PrintJobHandle> SubmitAsync(string jobName, string contentType, ReadOnlyMemory<byte> document, CancellationToken ct)
    {
        var id = ++_nextId;
        Submissions.Add((jobName, contentType, document.ToArray()));
        _active[id] = new PrintJobStatus(id, jobName, PrintJobState.Pending);
        return Task.FromResult(new PrintJobHandle(id));
    }

    public Task<IReadOnlyList<PrintJobStatus>> GetActiveJobsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PrintJobStatus>>(_active.Values.ToList());

    public Task CancelAsync(int jobId, CancellationToken ct)
    {
        Canceled.Add(jobId);
        _active.Remove(jobId);
        return Task.CompletedTask;
    }

    // Test helpers:
    public void CompleteJob(int jobId) => _active.Remove(jobId);

    public void SetActive(int jobId, string jobName) =>
        _active[jobId] = new PrintJobStatus(jobId, jobName, PrintJobState.Processing);

    public void SetState(int jobId, PrintJobState state)
    {
        if (_active.TryGetValue(jobId, out var job))
        {
            _active[jobId] = job with { State = state };
        }
    }
}