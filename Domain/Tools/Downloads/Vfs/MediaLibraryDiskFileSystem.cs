using System.Runtime.CompilerServices;
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
    DownloadsOverlay downloads) : DiskFileSystem(Name, Mount(root), client, root), ICrossMountMoveGuard
{
    public const string Name = "media";

    // Read off the parameter here rather than in a member body: the same value goes to the base
    // constructor, and capturing the parameter itself would leave two copies of the disk root.
    private readonly string _rootPath = root.BaseLibraryPath;

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
        + "task and cleans up its files. Also removes leftovers whose torrent is already gone: the "
        + "download directory, or a real status.json file no live download owns. Other media paths "
        + "cannot be deleted.";

    // The only text on this mount is the overlay's rendered status file; the media itself is bytes.
    public override async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
        await RefuseAsync<FsReadResult>(DownloadsIntent.TextRead, path, ct)
        ?? await downloads.ReadAsync(path, ct);

    public override async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        var disk = await base.GlobAsync(basePath, pattern, ct);
        // An active download's directory can predate its files on disk (queued, still fetching
        // metadata), and info already reports it as existing — so the disk's not_found is not this
        // mount's answer until the overlay has had its say. Every other disk error stands.
        if (!disk.TryGetValue(out var entries, out var diskError))
        {
            if (diskError.ErrorCode != ToolError.Codes.NotFound)
            {
                return disk;
            }

            entries = new FsGlobResult { Entries = [], Truncated = false, Total = 0 };
        }

        // The overlay matches mount-relative candidates, so it must see the pattern the disk matcher
        // ran, not the caller's absolute spelling of it — no candidate ever carries the root prefix,
        // so an absolute pattern used to list the real files and omit every virtual status.json.
        // Separators are normalized because virtual paths are always '/'-separated. A pattern
        // outside the glob root matched nothing on disk and owns no download either.
        var scoped = GlobFilesTool.ToMatcherRelative(
            GlobFilesTool.MatcherRoot(_rootPath, basePath), pattern);

        if (scoped is null)
        {
            return disk;
        }

        var overlay = await downloads.GlobEntriesAsync(basePath, scoped.Replace('\\', '/'), ct);
        if (!overlay.TryGetValue(out var virtualEntries, out var overlayError))
        {
            return new FsResult<FsGlobResult>.Err(overlayError);
        }

        // The overlay owning nothing leaves the disk's answer standing — including its not_found.
        return virtualEntries.Count == 0
            ? disk
            : new FsResult<FsGlobResult>.Ok(Merge(entries, virtualEntries));
    }

    public override async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
        await downloads.TryInfoAsync(path, ct) ?? await base.InfoAsync(path, ct);

    public override async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct) =>
        await downloads.TryDeleteAsync(path, ct) ?? await base.DeleteAsync(path, ct);

    // Delete is the download's cancel, so it is left to the overlay; move has no such meaning and a
    // live download whose directory moved keeps writing into one qBittorrent recreates. Both ends of
    // the move are asked, because the boundary is crossed just as badly on the way in.
    public override async Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
        Refuse<FsMoveResult>(sourcePath, destinationPath)
        ?? await RefuseActiveDownloadAsync<FsMoveResult>(sourcePath, destinationPath, ct)
        ?? await base.MoveAsync(sourcePath, destinationPath, ct);

    // A live download owns its directory and everything in it. Moving any of that out leaves the
    // download rewriting files the moved copy no longer tracks; moving anything in puts it where
    // delete-as-cancel destroys it. Either side of the move naming such a path is the refusal.
    private async Task<FsResult<T>?> RefuseActiveDownloadAsync<T>(
        string sourcePath, string destinationPath, CancellationToken ct) where T : class
    {
        var offender =
            await downloads.TouchesActiveDownloadAsync(sourcePath, ct) ? sourcePath
            : await downloads.TouchesActiveDownloadAsync(destinationPath, ct) ? destinationPath
            : null;

        return offender is null ? null : new FsResult<T>.Err(ActiveDownloadRefusal(offender));
    }

    // The cross-mount half of the same refusal. A move between two mounts never reaches MoveAsync —
    // it streams the payload out and then deletes the source, and on this mount that delete is the
    // download's cancel — so VfsMoveTool asks each end about its own path first, and the answer here
    // is the envelope the same-mount refusal already returns.
    public async Task<ToolErrorResult?> RefuseMoveAsync(string relativePath, CancellationToken ct) =>
        await downloads.TouchesActiveDownloadAsync(relativePath, ct)
            ? ActiveDownloadRefusal(relativePath)
            : null;

    private static ToolErrorResult ActiveDownloadRefusal(string offender) => new()
    {
        ErrorCode = ToolError.Codes.UnsupportedOperation,
        Message = $"'{offender}' belongs to an active download; moving across that boundary would "
                  + "leave the download writing into files the move cannot follow, and anything moved "
                  + "inside is removed when the download is cancelled.",
        Retryable = false,
        Hint = $"Delete {MediaFilesystem.DownloadsSubdir}/<id> to cancel the download, or wait "
               + "for it to finish, then move the files."
    };

    // Copy and blob-write only land content, so unlike move only the destination side of the
    // boundary is asked: the way out is harmless (the source keeps its file), but whatever lands
    // inside a live download's directory is removed the moment the download is cancelled.
    public override async Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        await RefuseAsync<FsCopyResult>(DownloadsIntent.Land, destinationPath, ct)
        ?? await base.CopyAsync(sourcePath, destinationPath, overwrite, createDirectories, ct);

    // The virtual file only exists while a download owns the id. A leftover real status.json with
    // no live download is the disk's file — refusing to read it left it visible and removable yet
    // unreadable, so the refusal asks for liveness instead of the path's spelling.
    public override async Task<FsResult<FsBlobReadResult>> ReadBlobAsync(
        string path, long offset, int length, CancellationToken ct) =>
        await RefuseAsync<FsBlobReadResult>(DownloadsIntent.ByteRead, path, ct)
        ?? await base.ReadBlobAsync(path, offset, length, ct);

    public override async Task<FsResult<FsBlobWriteResult>> WriteBlobAsync(
        string path, string contentBase64, long offset, bool overwrite, bool createDirectories, CancellationToken ct) =>
        await RefuseAsync<FsBlobWriteResult>(DownloadsIntent.Land, path, ct)
        ?? await base.WriteBlobAsync(path, contentBase64, offset, overwrite, createDirectories, ct);

    // The streamed halves of the same two operations, asking the same rule with the same intent —
    // which is what makes the two halves unable to disagree. They throw rather than answer an
    // envelope because that is the shape the chunk contract has, and VfsCopyTool turns the typed
    // exception back into the envelope the ranged half returns directly.
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        if (await downloads.RefuseAsync(DownloadsIntent.ByteRead, path, ct) is { } refusal)
        {
            throw new FileSystemOperationException(refusal);
        }

        await foreach (var chunk in base.ReadChunksAsync(path, ct).WithCancellation(ct))
        {
            yield return chunk;
        }
    }

    // The streamed half of the landing refusal: a cross-mount copy-in never calls CopyAsync or
    // WriteBlobAsync, it streams — the typed exception carries the same envelope through.
    public override async Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        if (await downloads.RefuseAsync(DownloadsIntent.Land, path, ct) is { } refusal)
        {
            throw new FileSystemOperationException(refusal);
        }

        return await base.WriteChunksAsync(path, chunks, overwrite, createDirectories, ct);
    }

    // status.json is a rendered view of live download state, not a file on disk: moving, copying or
    // writing it would silently produce a stale snapshot under a name that still looks live.
    private const string VirtualFileRefusal =
        "status.json is a virtual read-only file; read it with fs_read — it cannot be moved, copied, or written.";

    private FsResult<T>? Refuse<T>(params string[] paths) where T : class =>
        paths.Any(downloads.IsVirtualPath)
            ? FsError.Fail<T>(ToolError.Codes.UnsupportedOperation, VirtualFileRefusal)
            : null;

    // Every operation on this mount asks the overlay's one rule before it acts, and falls through
    // to the disk beneath when the rule says nothing.
    private async Task<FsResult<T>?> RefuseAsync<T>(DownloadsIntent intent, string path, CancellationToken ct)
        where T : class =>
        await downloads.RefuseAsync(intent, path, ct) is { } refusal ? new FsResult<T>.Err(refusal) : null;

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