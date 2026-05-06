using System.Text.Json.Nodes;

namespace Domain.Tools.Files;

public class BlobWriteTool(string rootPath)
{
    private static readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    protected const string Description = """
        Writes a chunk of raw bytes (base64-encoded) to a file at the given offset.
        Used by the agent's cross-filesystem transfer machinery to stream binary content.
        offset=0 creates (or, with overwrite=true, truncates) the file; later calls append at offset.
        Returns { path, bytesWritten, totalBytes }.
        """;

    protected JsonNode Run(string path, string contentBase64, long offset, bool overwrite, bool createDirectories)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var resolved = ResolveAndValidate(path);
        var bytes = Convert.FromBase64String(contentBase64);

        if (createDirectories)
        {
            var parent = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }

        if (offset == 0)
        {
            if (File.Exists(resolved) && !overwrite)
            {
                throw new IOException($"File already exists: {path}");
            }
            File.WriteAllBytes(resolved, bytes);
        }
        else
        {
            using var stream = new FileStream(resolved, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            stream.Seek(offset, SeekOrigin.Begin);
            stream.Write(bytes, 0, bytes.Length);
        }

        var info = new FileInfo(resolved);
        return new JsonObject
        {
            ["path"] = path,
            ["bytesWritten"] = bytes.Length,
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

        if (fullPath.Equals(canonicalRoot, _pathComparison) ||
            fullPath.StartsWith(rootWithSep, _pathComparison))
        {
            return fullPath;
        }
        throw new UnauthorizedAccessException($"Access denied: path must be within {canonicalRoot}");
    }
}
