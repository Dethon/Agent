using Domain.DTOs.Printing;

namespace Domain.Contracts;

// Disk-backed buffer for queued documents. Bytes accumulate via offset-based writes
// (the fs_blob_write protocol has no EOF signal); metadata is tracked per filename.
public interface IPrintSpool
{
    Task WriteBytesAsync(string fileName, string contentType, ReadOnlyMemory<byte> bytes,
        long offset, bool overwrite, CancellationToken ct);

    Task<(byte[] Bytes, bool Eof, long TotalBytes)> ReadBytesAsync(string fileName, long offset, int length, CancellationToken ct);

    Task<byte[]?> ReadAllBytesAsync(string fileName, CancellationToken ct);

    Task<SpoolEntry?> GetAsync(string fileName, CancellationToken ct);

    Task<IReadOnlyList<SpoolEntry>> ListAsync(CancellationToken ct);

    Task MarkSubmittedAsync(string fileName, int jobId, DateTimeOffset submittedAt, CancellationToken ct);

    Task RemoveAsync(string fileName, CancellationToken ct);
}