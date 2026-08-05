using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.Files;

namespace Domain.Tools.Downloads.Vfs;

// The media library: a disk root with the downloads overlay layered on top of the same directory.
// Every active download surfaces a virtual downloads/<id>/status.json that is not on disk, and
// deleting downloads/<id> cancels the download rather than trashing a directory. Payload files
// inside a download directory stay plain disk entries, which is why this extends the disk root
// instead of replacing it.
public sealed class MediaLibraryDiskFileSystem(
    IFileSystemClient client,
    LibraryPathConfig root,
    DownloadsOverlay downloads) : DiskFileSystem(Name, Mount(root), client, root)
{
    public const string Name = "media";

    // Composed here rather than handed in like the generic disk root's: this type is the media
    // library, so its prose belongs with it, and it already holds the path that text names.
    private static string Mount(LibraryPathConfig root) =>
        $"Media library ({root.BaseLibraryPath}) — books, audiobooks, and other downloaded media. "
        + "Read/list focused; treat writes as organisational only. Does NOT support fs_exec. Active "
        + $"downloads live under {MediaFilesystem.DownloadsDir}/<id>/: a virtual read-only "
        + "status.json reports live state/progress/eta, and deleting the <id> directory cancels the "
        + "download and cleans up its files.";

    public override string DescribeRead =>
        $"Read a download's virtual status file ({MediaFilesystem.DownloadsSubdir}/<id>/status.json "
        + "— live state, progress, eta). Other media files are not text-readable; use fs_blob_read "
        + "for raw bytes.";

    public override string DescribeDelete =>
        $"Delete a download directory ({MediaFilesystem.DownloadsSubdir}/<id>): cancels the torrent "
        + "task and cleans up its files. Also removes leftover download directories whose torrent is "
        + "already gone. Other media paths cannot be deleted.";

    // The only text on this mount is the overlay's rendered status file; the media itself is bytes.
    public override async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
        await downloads.TryReadAsync(path, ct)
        ?? FsError.Fail<FsReadResult>(ToolError.Codes.UnsupportedOperation,
            $"fs_read on the {FilesystemName} filesystem only reads "
            + $"{MediaFilesystem.DownloadsSubdir}/<id>/status.json.");

    public override async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        var disk = await base.GlobAsync(basePath, pattern, ct);
        if (!disk.TryGetValue(out var entries, out _))
        {
            return disk;
        }

        // The overlay matches mount-relative candidates, so it must see the pattern the disk matcher
        // ran, not the caller's absolute spelling of it — no candidate ever carries the root prefix,
        // so an absolute pattern used to list the real files and omit every virtual status.json.
        // Separators are normalized because virtual paths are always '/'-separated. A pattern
        // outside the glob root matched nothing on disk and owns no download either.
        var scoped = GlobFilesTool.ToMatcherRelative(
            GlobFilesTool.MatcherRoot(root.BaseLibraryPath, basePath), pattern);

        return scoped is null
            ? disk
            : new FsResult<FsGlobResult>.Ok(Merge(
                entries, await downloads.GlobEntriesAsync(basePath, scoped.Replace('\\', '/'), ct)));
    }

    public override async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
        await downloads.TryInfoAsync(path, ct) ?? await base.InfoAsync(path, ct);

    public override Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct) =>
        downloads.DeleteAsync(path, ct);

    // Delete is the download's cancel, so it is left to the overlay; move has no such meaning and a
    // live download whose directory moved keeps writing into one qBittorrent recreates.
    public override async Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
        Refuse<FsMoveResult>(sourcePath, destinationPath)
        ?? (await downloads.HoldsActiveDownloadAsync(sourcePath, ct)
            ? FsError.Fail<FsMoveResult>(ToolError.Codes.UnsupportedOperation,
                $"'{sourcePath}' holds an active download; moving it would leave the download writing "
                + "into a directory it recreates, and the moved copy orphaned.",
                retryable: false,
                hint: $"Delete {MediaFilesystem.DownloadsSubdir}/<id> to cancel the download, or wait "
                      + "for it to finish, then move the files.")
            : await base.MoveAsync(sourcePath, destinationPath, ct));

    public override async Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        Refuse<FsCopyResult>(sourcePath, destinationPath)
        ?? await base.CopyAsync(sourcePath, destinationPath, overwrite, createDirectories, ct);

    public override async Task<FsResult<FsBlobReadResult>> ReadBlobAsync(
        string path, long offset, int length, CancellationToken ct) =>
        Refuse<FsBlobReadResult>(path) ?? await base.ReadBlobAsync(path, offset, length, ct);

    public override async Task<FsResult<FsBlobWriteResult>> WriteBlobAsync(
        string path, string contentBase64, long offset, bool overwrite, bool createDirectories, CancellationToken ct) =>
        Refuse<FsBlobWriteResult>(path)
        ?? await base.WriteBlobAsync(path, contentBase64, offset, overwrite, createDirectories, ct);

    // The streamed halves of the same two operations. A cross-mount copy never calls the ranged blob
    // tools — it streams — so a refusal installed on those alone let a real file land where the
    // virtual status.json is: invisible afterwards (the overlay shadows reads, Merge dedupes globs)
    // and unremovable (delete on that path answers "read-only"). These throw rather than answer an
    // envelope because that is the shape the two chunk operations have; VfsCopyTool turns a
    // NotSupportedException back into the same unsupported-operation envelope.
    public override IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(string path, CancellationToken ct) =>
        downloads.IsVirtualPath(path)
            ? throw new NotSupportedException(VirtualFileRefusal)
            : base.ReadChunksAsync(path, ct);

    public override Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        downloads.IsVirtualPath(path)
            ? throw new NotSupportedException(VirtualFileRefusal)
            : base.WriteChunksAsync(path, chunks, overwrite, createDirectories, ct);

    // status.json is a rendered view of live download state, not a file on disk: moving, copying or
    // writing it would silently produce a stale snapshot under a name that still looks live.
    private const string VirtualFileRefusal =
        "status.json is a virtual read-only file; read it with fs_read — it cannot be moved, copied, or written.";

    private FsResult<T>? Refuse<T>(params string[] paths) where T : class =>
        paths.Any(downloads.IsVirtualPath)
            ? FsError.Fail<T>(ToolError.Codes.UnsupportedOperation, VirtualFileRefusal)
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