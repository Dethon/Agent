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
    DownloadsOverlay downloads) : DiskFileSystem(Name, MountDescription, client, root)
{
    public const string Name = "media";

    // Not a constructor argument like the generic disk root's: this type is the media library, so
    // its prose belongs with it.
    private const string MountDescription =
        "Media library — books, audiobooks, and other downloaded media. Read/list focused; treat "
        + "writes as organisational only. Does NOT support fs_exec. Active downloads live under "
        + "/media/downloads/<id>/: a virtual read-only status.json reports live state/progress/eta, "
        + "and deleting the <id> directory cancels the download and cleans up its files.";

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

        return new FsResult<FsGlobResult>.Ok(
            Merge(entries, await downloads.GlobEntriesAsync(basePath, pattern, ct)));
    }

    public override async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
        await downloads.TryInfoAsync(path, ct) ?? await base.InfoAsync(path, ct);

    public override Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct) =>
        downloads.DeleteAsync(path, ct);

    public override async Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
        Refuse<FsMoveResult>(sourcePath, destinationPath)
        ?? await base.MoveAsync(sourcePath, destinationPath, ct);

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

    // status.json is a rendered view of live download state, not a file on disk: moving, copying or
    // writing it would silently produce a stale snapshot under a name that still looks live.
    private FsResult<T>? Refuse<T>(params string[] paths) where T : class =>
        paths.Any(downloads.IsVirtualPath)
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