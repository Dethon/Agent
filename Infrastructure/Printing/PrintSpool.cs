using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Printing;

namespace Infrastructure.Printing;

public sealed class PrintSpool(string rootPath, TimeProvider clock) : IPrintSpool
{
    private const string BlobSuffix = ".blob";
    private const string MetaSuffix = ".meta.json";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task WriteBytesAsync(string fileName, string contentType, ReadOnlyMemory<byte> bytes,
        long offset, bool overwrite, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(rootPath);
            var blobPath = BlobPath(fileName);

            if (offset == 0)
            {
                await File.WriteAllBytesAsync(blobPath, bytes.ToArray(), ct);
            }
            else
            {
                await using var stream = new FileStream(blobPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                stream.Seek(offset, SeekOrigin.Begin);
                await stream.WriteAsync(bytes, ct);
            }

            var size = new FileInfo(blobPath).Length;
            var existing = await ReadMetaAsync(fileName, ct);
            var entry = new SpoolEntry
            {
                FileName = fileName,
                ContentType = offset == 0 ? contentType : existing?.ContentType ?? contentType,
                SizeBytes = size,
                LastWriteAt = clock.GetUtcNow(),
                // A fresh offset-0 write restarts the lifecycle; a re-write clears any prior submission.
                SubmittedAt = offset == 0 ? null : existing?.SubmittedAt,
                JobId = offset == 0 ? null : existing?.JobId,
                MissingSince = offset == 0 ? null : existing?.MissingSince
            };
            await WriteMetaAsync(entry, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(byte[] Bytes, bool Eof, long TotalBytes)> ReadBytesAsync(string fileName, long offset, int length, CancellationToken ct)
    {
        var blobPath = BlobPath(fileName);
        if (!File.Exists(blobPath))
        {
            return (Array.Empty<byte>(), true, 0);
        }

        var total = new FileInfo(blobPath).Length;
        var available = Math.Max(0, total - offset);
        var toRead = (int)Math.Min(length, available);
        var buffer = new byte[toRead];
        if (toRead > 0)
        {
            await using var stream = File.OpenRead(blobPath);
            stream.Seek(offset, SeekOrigin.Begin);
            var read = 0;
            while (read < toRead)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, toRead - read), ct);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }
        }

        return (buffer, offset + toRead >= total, total);
    }

    public async Task<byte[]?> ReadAllBytesAsync(string fileName, CancellationToken ct)
    {
        var blobPath = BlobPath(fileName);
        return File.Exists(blobPath) ? await File.ReadAllBytesAsync(blobPath, ct) : null;
    }

    public Task<SpoolEntry?> GetAsync(string fileName, CancellationToken ct) => ReadMetaAsync(fileName, ct);

    public async Task<IReadOnlyList<SpoolEntry>> ListAsync(CancellationToken ct)
    {
        if (!Directory.Exists(rootPath))
        {
            return [];
        }

        var names = Directory.EnumerateFiles(rootPath, "*" + MetaSuffix)
            .Select(p => Path.GetFileName(p)[..^MetaSuffix.Length]);

        var entries = await Task.WhenAll(names.Select(async escaped =>
            await ReadMetaByEscapedAsync(escaped, ct)));

        return entries.Where(e => e is not null).Select(e => e!).ToList();
    }

    public async Task MarkSubmittedAsync(string fileName, int jobId, DateTimeOffset submittedAt, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var existing = await ReadMetaAsync(fileName, ct);
            if (existing is null)
            {
                return;
            }

            await WriteMetaAsync(existing with { JobId = jobId, SubmittedAt = submittedAt }, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetMissingSinceAsync(string fileName, DateTimeOffset? missingSince, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var existing = await ReadMetaAsync(fileName, ct);
            if (existing is null)
            {
                return;
            }

            await WriteMetaAsync(existing with { MissingSince = missingSince }, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string fileName, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            File.Delete(BlobPath(fileName));
            File.Delete(MetaPath(fileName));
        }
        finally
        {
            _lock.Release();
        }
    }

    private string BlobPath(string fileName) => Path.Combine(rootPath, Uri.EscapeDataString(fileName) + BlobSuffix);
    private string MetaPath(string fileName) => Path.Combine(rootPath, Uri.EscapeDataString(fileName) + MetaSuffix);

    private async Task<SpoolEntry?> ReadMetaAsync(string fileName, CancellationToken ct)
    {
        var metaPath = MetaPath(fileName);
        if (!File.Exists(metaPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(metaPath);
        return await JsonSerializer.DeserializeAsync<SpoolEntry>(stream, _json, ct);
    }

    private async Task<SpoolEntry?> ReadMetaByEscapedAsync(string escapedName, CancellationToken ct)
        => await ReadMetaAsync(Uri.UnescapeDataString(escapedName), ct);

    private async Task WriteMetaAsync(SpoolEntry entry, CancellationToken ct)
    {
        await using var stream = File.Create(MetaPath(entry.FileName));
        await JsonSerializer.SerializeAsync(stream, entry, _json, ct);
    }
}