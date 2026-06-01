using Domain.DTOs.Printing;

namespace Domain.Contracts;

public interface IPrinterClient
{
    Task<PrintJobHandle> SubmitAsync(string jobName, string contentType, ReadOnlyMemory<byte> document, CancellationToken ct);

    Task<IReadOnlyList<PrintJobStatus>> GetActiveJobsAsync(CancellationToken ct);

    Task CancelAsync(int jobId, CancellationToken ct);
}