using Domain.DTOs.FileSystem;

namespace Domain.Tools.Files;

public class BlobReadTool(string rootPath)
{
    public const int MaxChunkSizeBytes = 256 * 1024;

    private readonly PathJail _jail = new(rootPath);

    protected const string Description = """
        Reads a chunk of raw bytes from a file as base64. Used by the agent's cross-filesystem
        transfer machinery to stream binary content. `length` is clamped to 256 KiB per call.
        Returns { contentBase64, eof, totalBytes }.
        """;

    public FsResult<FsBlobReadResult> Run(string path, long offset, int length)
    {
        if (!_jail.TryResolve(path, out var resolved))
        {
            return FsError.Invalid<FsBlobReadResult>(_jail.DeniedMessage);
        }

        if (offset < 0 || length < 0)
        {
            return FsError.Invalid<FsBlobReadResult>("offset and length must not be negative.");
        }

        if (!File.Exists(resolved))
        {
            return FsError.NotFound<FsBlobReadResult>(path);
        }

        var info = new FileInfo(resolved);
        var clampedLength = Math.Min(length, MaxChunkSizeBytes);
        var available = Math.Max(0, info.Length - offset);
        var toRead = (int)Math.Min(clampedLength, available);

        var buffer = new byte[toRead];
        var actuallyRead = 0;
        if (toRead > 0)
        {
            using var stream = File.OpenRead(resolved);
            stream.Seek(offset, SeekOrigin.Begin);
            while (actuallyRead < toRead)
            {
                var n = stream.Read(buffer, actuallyRead, toRead - actuallyRead);
                if (n == 0)
                {
                    break;
                }

                actuallyRead += n;
            }
        }

        return new FsResult<FsBlobReadResult>.Ok(new FsBlobReadResult
        {
            ContentBase64 = Convert.ToBase64String(buffer, 0, actuallyRead),
            Eof = offset + actuallyRead >= info.Length,
            TotalBytes = info.Length
        });
    }
}