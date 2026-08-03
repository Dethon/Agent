using System.Text.Json.Nodes;
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

    protected JsonNode Run(string path, long offset, int length)
    {
        var resolved = _jail.Resolve(path);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException($"File not found: {path}");
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

        var eof = offset + actuallyRead >= info.Length;
        return FsResultContract.ToNode(new FsBlobReadResult
        {
            ContentBase64 = Convert.ToBase64String(buffer, 0, actuallyRead),
            Eof = eof,
            TotalBytes = info.Length
        });
    }
}