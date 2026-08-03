using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;

namespace Domain.Tools.Files;

// A backend over a real directory on disk: the operations any disk root can perform, composed from
// the disk tool classes. An optional downloads overlay layers virtual entries onto the same root —
// the library's downloads view — which is why this stays a composition rather than a root-path
// wrapper. Text create/edit/search are not here: a root without allowed text extensions cannot do
// them, and capability is decided by which methods a backend overrides.
public class DiskFileSystem(
    string filesystemName,
    IFileSystemClient client,
    LibraryPathConfig root,
    DownloadsOverlay? downloads = null) : FileSystemBackendBase
{
    public override string FilesystemName => filesystemName;

    protected LibraryPathConfig Root { get; } = root;

    private readonly GlobFilesTool _glob = new(client, root);
    private readonly MoveTool _move = new(client, root);
    private readonly RemoveTool _remove = new(client, root);
    private readonly CopyTool _copy = new(root.BaseLibraryPath);
    private readonly FileInfoTool _info = new(root.BaseLibraryPath);
    private readonly BlobReadTool _blobRead = new(root.BaseLibraryPath);
    private readonly BlobWriteTool _blobWrite = new(root.BaseLibraryPath);

    public override string DescribeGlob =>
        "Searches for files and directories matching a glob pattern relative to the mount root. "
        + "`*` matches one path segment, `**` recurses, `?` matches one character. Brace alternation "
        + "expands too: `**/*.{jpg,png,gif}` matches any of the listed extensions. A trailing slash "
        + "matches directories only (e.g. `*/`, `src/**/`); otherwise both files and directories "
        + "match, with directory results returned with a trailing slash so you can tell them apart. "
        + "Results are capped at 200; the response is `{entries, truncated, total}`. An empty result "
        + "means nothing matched—refine the pattern.";

    public override string DescribeInfo =>
        "Returns metadata about a path: exists, isDirectory, size (files only), and lastModified. "
        + "Use as a cheap guard before read/edit/move/delete to avoid errors on missing paths. "
        + "Works for files and directories; never fails on missing paths — returns exists=false instead.";

    public override string DescribeMove =>
        "Moves and/or renames a file or directory. Both arguments can be absolute paths under the "
        + "mount root, or relative paths (resolved against it). Equivalent to 'mv -T {SourcePath} "
        + "{DestinationPath}'. The destination path must not exist. Parent directories are created "
        + "automatically.";

    public override string DescribeDelete =>
        "Removes a file or directory by moving it to a trash folder. The path can be absolute "
        + "(under the mount root) or relative (resolved against it).";

    public override string DescribeCopy =>
        "Copies a file or directory within this filesystem. Both arguments can be absolute paths "
        + "under the mount root, or relative paths (resolved against it). Source must exist; if "
        + "destination exists, overwrite must be true. Parent directories are created automatically "
        + "when createDirectories=true (default).";

    public override string DescribeBlobRead =>
        "Reads a chunk of raw bytes from a file as base64. Used by the agent's cross-filesystem "
        + "transfer machinery to stream binary content. `length` is clamped to 256 KiB per call. "
        + "Returns { contentBase64, eof, totalBytes }.";

    public override string DescribeBlobWrite =>
        "Writes a chunk of raw bytes (base64-encoded) to a file at the given offset. Used by the "
        + "agent's cross-filesystem transfer machinery to stream binary content. offset=0 creates "
        + "(or, with overwrite=true, truncates) the file; later calls append at offset. "
        + "Returns { path, bytesWritten, totalBytes }.";

    // The overlay owns downloads/<id>/status.json; everything else on the same root is a plain
    // disk entry. A root without an overlay reads through the disk tools alone.
    public override async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        var disk = await _glob.Run(pattern, ct, basePath);
        if (downloads is null || !disk.TryGetValue(out var entries, out _))
        {
            return disk;
        }

        return new FsResult<FsGlobResult>.Ok(
            Merge(entries, await downloads.GlobEntriesAsync(basePath, pattern, ct)));
    }

    public override async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        if (downloads is not null && await downloads.TryInfoAsync(path, ct) is { } overlay)
        {
            return overlay;
        }

        return _info.Run(path);
    }

    public override async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        if (downloads is not null && await downloads.TryReadAsync(path, ct) is { } overlay)
        {
            return overlay;
        }

        return ReadFromDisk(path, offset, limit);
    }

    // A root with no text tooling has nothing to read as text beyond what the overlay serves.
    protected virtual FsResult<FsReadResult> ReadFromDisk(string path, int? offset, int? limit) =>
        FsError.Fail<FsReadResult>(ToolError.Codes.UnsupportedOperation,
            $"fs_read on the {filesystemName} filesystem only reads {MediaFilesystem.DownloadsSubdir}/<id>/status.json.");

    public override async Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        if (VirtualPathRefusal<FsMoveResult>(sourcePath, destinationPath) is { } refusal)
        {
            return refusal;
        }

        return await _move.Run(sourcePath, destinationPath, ct);
    }

    public override async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct) =>
        downloads is not null ? await downloads.DeleteAsync(path, ct) : await _remove.Run(path, ct);

    public override Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        Task.FromResult(VirtualPathRefusal<FsCopyResult>(sourcePath, destinationPath)
            ?? _copy.Run(sourcePath, destinationPath, overwrite, createDirectories));

    // A disk root has real random access, so the ranged blob tools go straight at it rather than
    // draining the chunk stream the base default would have to.
    public override Task<FsResult<FsBlobReadResult>> ReadBlobAsync(
        string path, long offset, int length, CancellationToken ct) =>
        Task.FromResult(VirtualPathRefusal<FsBlobReadResult>(path) ?? _blobRead.Run(path, offset, length));

    public override Task<FsResult<FsBlobWriteResult>> WriteBlobAsync(
        string path, string contentBase64, long offset, bool overwrite, bool createDirectories, CancellationToken ct) =>
        Task.FromResult(VirtualPathRefusal<FsBlobWriteResult>(path)
            ?? _blobWrite.Run(path, contentBase64, offset, overwrite, createDirectories));

    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        long offset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!_blobRead.Run(path, offset, BlobReadTool.MaxChunkSizeBytes).TryGetValue(out var chunk, out var error))
            {
                throw new IOException(error.Message);
            }

            var bytes = Convert.FromBase64String(chunk.ContentBase64);
            if (bytes.Length > 0)
            {
                offset += bytes.Length;
                yield return bytes;
            }

            if (chunk.Eof || bytes.Length == 0)
            {
                yield break;
            }
        }
    }

    public override async Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        long offset = 0;
        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            Write(chunk.Span, offset);
            offset += chunk.Length;
        }

        if (offset == 0)
        {
            Write(ReadOnlySpan<byte>.Empty, 0);
        }

        return offset;

        void Write(ReadOnlySpan<byte> bytes, long at)
        {
            var written = _blobWrite.Run(path, Convert.ToBase64String(bytes), at, overwrite, createDirectories);
            if (!written.TryGetValue(out _, out var error))
            {
                throw new IOException(error.Message);
            }
        }
    }

    // status.json is a rendered view of live download state, not a file on disk: moving, copying or
    // writing it would silently produce a stale snapshot under a name that looks live.
    private FsResult<T>? VirtualPathRefusal<T>(params string[] paths) where T : class =>
        downloads is not null && paths.Any(downloads.IsVirtualPath)
            ? FsError.Fail<T>(ToolError.Codes.UnsupportedOperation,
                "status.json is a virtual read-only file; read it with fs_read — it cannot be moved, copied, or written.")
            : null;

    private static FsGlobResult Merge(FsGlobResult disk, IReadOnlyList<string> virtualEntries)
    {
        var added = virtualEntries.Except(disk.Entries, StringComparer.Ordinal).ToList();
        if (added.Count == 0)
        {
            return disk;
        }

        var combined = disk.Entries.Concat(added).ToList();
        return new FsGlobResult
        {
            Entries = combined.Take(GlobFilesTool.FileResultCap).ToList(),
            Truncated = disk.Truncated || combined.Count > GlobFilesTool.FileResultCap,
            Total = disk.Total + added.Count
        };
    }
}