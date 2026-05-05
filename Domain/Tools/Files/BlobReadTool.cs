using System.Text.Json.Nodes;

namespace Domain.Tools.Files;

public class BlobReadTool(string rootPath)
{
    public const int MaxChunkSizeBytes = 256 * 1024;

    protected const string Description = """
        Reads a chunk of raw bytes from a file as base64. Used by the agent's cross-filesystem
        transfer machinery to stream binary content. `length` is clamped to 256 KiB per call.
        Returns { contentBase64, eof, totalBytes }.
        """;

    protected JsonNode Run(string path, long offset, int length)
    {
        var resolved = ResolveAndValidate(path);
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException($"File not found: {path}");
        }

        var info = new FileInfo(resolved);
        var clampedLength = Math.Min(length, MaxChunkSizeBytes);
        var available = Math.Max(0, info.Length - offset);
        var toRead = (int)Math.Min(clampedLength, available);

        var buffer = new byte[toRead];
        if (toRead > 0)
        {
            using var stream = File.OpenRead(resolved);
            stream.Seek(offset, SeekOrigin.Begin);
            var read = 0;
            while (read < toRead)
            {
                var n = stream.Read(buffer, read, toRead - read);
                if (n == 0) break;
                read += n;
            }
        }

        var eof = offset + toRead >= info.Length;
        return new JsonObject
        {
            ["contentBase64"] = Convert.ToBase64String(buffer),
            ["eof"] = eof,
            ["totalBytes"] = info.Length
        };
    }

    private string ResolveAndValidate(string path)
    {
        var normalized = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(rootPath, normalized));

        var canonicalRoot = Path.GetFullPath(rootPath);
        var rootWithSep = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;

        if (fullPath.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }
        throw new UnauthorizedAccessException($"Access denied: path must be within {canonicalRoot}");
    }
}
