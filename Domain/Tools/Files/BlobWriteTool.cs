using Domain.DTOs.FileSystem;

namespace Domain.Tools.Files;

public class BlobWriteTool(string rootPath)
{
    private readonly PathJail _jail = new(rootPath);

    protected const string Description = """
        Writes a chunk of raw bytes (base64-encoded) to a file at the given offset.
        Used by the agent's cross-filesystem transfer machinery to stream binary content.
        offset=0 creates (or, with overwrite=true, truncates) the file; later calls append at offset.
        Returns { path, bytesWritten, totalBytes }.
        """;

    protected FsResult<FsBlobWriteResult> Run(string path, string contentBase64, long offset, bool overwrite, bool createDirectories)
    {
        if (offset < 0)
        {
            return FsError.Invalid<FsBlobWriteResult>("offset must not be negative.");
        }

        if (!_jail.TryResolve(path, out var resolved))
        {
            return FsError.Invalid<FsBlobWriteResult>(_jail.DeniedMessage);
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(contentBase64);
        }
        catch (FormatException ex)
        {
            return FsError.Invalid<FsBlobWriteResult>($"contentBase64 is not valid base64: {ex.Message}");
        }

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
                return FsError.AlreadyExists<FsBlobWriteResult>($"File already exists: {path}");
            }
            File.WriteAllBytes(resolved, bytes);
        }
        else
        {
            using var stream = new FileStream(resolved, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            stream.Seek(offset, SeekOrigin.Begin);
            stream.Write(bytes, 0, bytes.Length);
        }

        return new FsResult<FsBlobWriteResult>.Ok(new FsBlobWriteResult
        {
            Path = path,
            BytesWritten = bytes.Length,
            TotalBytes = new FileInfo(resolved).Length
        });
    }
}