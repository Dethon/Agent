using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Downloads.Vfs;

// Overlays download semantics on the media filesystem's downloads/ subtree: every active
// download surfaces a virtual read-only downloads/<id>/status.json, and deleting
// downloads/<id> cancels the download and cleans up its files. Payload files inside a
// download directory stay plain disk entries served by the regular media tools, so the
// Try* methods return null for paths the overlay does not own.
public sealed class DownloadsOverlay(
    IDownloadClient downloadClient,
    IDownloadRoutingStore routingStore,
    IFileSystemClient fileSystemClient,
    LibraryPathConfig libraryPath)
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public bool IsVirtualPath(string path) => ParseNode(path).Kind == DownloadNodeKind.StatusFile;

    // Virtual AND currently owned by a live download — the distinction that decides whether a
    // status.json path is the rendered view or a leftover real file the disk should serve.
    public async Task<bool> IsLiveVirtualPathAsync(string path, CancellationToken ct) =>
        ParseNode(path) is { Kind: DownloadNodeKind.StatusFile, Id: { } id }
        && await downloadClient.GetDownloadItem(id, ct) is not null;

    // True when this path and a live download's directory overlap: the directory itself, any
    // ancestor of it (moving a parent takes the directory with it), and anything under it (the
    // payload files the download is still writing). Deleting such a path is the documented cancel,
    // but moving it is not — on the way out the download keeps writing and recreates what it lost,
    // leaving the moved copy orphaned, and on the way in whatever landed inside is destroyed the
    // moment the download is cancelled.
    public async Task<bool> TouchesActiveDownloadAsync(string path, CancellationToken ct)
    {
        // The same canonical spelling the classifier uses, so 'downloads/./42' overlaps the live
        // download it names instead of reading as an unrelated string.
        if (DownloadsPath.Canonicalize(ToMountRelative(path)) is not { } candidate)
        {
            return false;
        }

        var items = await downloadClient.GetDownloadItems(ct);
        return items
            .Select(i => $"{MediaFilesystem.DownloadsSubdir}/{i.Id}")
            .Any(dir => candidate.Length == 0
                        || dir.Equals(candidate, StringComparison.Ordinal)
                        || dir.StartsWith(candidate + "/", StringComparison.Ordinal)
                        || candidate.StartsWith(dir + "/", StringComparison.Ordinal));
    }

    public async Task<FsResult<FsReadResult>?> TryReadAsync(string path, CancellationToken ct)
    {
        var node = ParseNode(path);
        if (node.Kind != DownloadNodeKind.StatusFile)
        {
            return null;
        }

        var item = await downloadClient.GetDownloadItem(node.Id!.Value, ct);
        if (item is null)
        {
            return await ReadLeftoverAsync(path, node.Id.Value, ct);
        }

        return Read(path, RenderStatus(item));
    }

    // No live download owns the id, but info and delete already treat a real file left at this
    // path as the disk's; reads must agree, or the leftover is visible and removable yet
    // unreadable.
    private async Task<FsResult<FsReadResult>> ReadLeftoverAsync(string path, int id, CancellationToken ct)
    {
        var file = Path.Combine(DiskDir(id), DownloadsPath.StatusFileName);
        return File.Exists(file)
            ? Read(path, await File.ReadAllTextAsync(file, ct))
            : new FsResult<FsReadResult>.Err(Error(ToolError.Codes.NotFound, $"Path not found: {path}"));
    }

    private static FsResult<FsReadResult> Read(string path, string content) =>
        new FsResult<FsReadResult>.Ok(new FsReadResult
        {
            FilePath = path,
            Content = content,
            TotalLines = content.Split('\n').Length,
            Truncated = false
        });

    public async Task<FsResult<FsInfoResult>?> TryInfoAsync(string path, CancellationToken ct)
    {
        var node = ParseNode(path);
        switch (node.Kind)
        {
            // Only a live download has a virtual status file. With no item owning the id the
            // overlay owns nothing here, so the disk underneath answers — otherwise a real file
            // left at this path is reported as not existing and can never be found.
            case DownloadNodeKind.StatusFile when await downloadClient.GetDownloadItem(node.Id!.Value, ct) is { } item:
                return new FsResult<FsInfoResult>.Ok(new FsInfoResult
                {
                    Exists = true,
                    Path = path,
                    IsDirectory = false,
                    Size = RenderStatus(item).Length
                });
            case DownloadNodeKind.DownloadDir when await downloadClient.GetDownloadItem(node.Id!.Value, ct) is not null:
                return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = path, IsDirectory = true });
            default:
                return null;
        }
    }

    // The overlay half of a media glob, under the same two guards as every other mount: the shared
    // prologue answers the invalid-argument envelope for a pattern past the brace-expansion cap, and
    // a pathological pattern that backtracks while matching ends as the timeout envelope instead of
    // an exception out of fs_glob. Candidates are library-root-relative, the same convention the
    // disk glob results use, and the scope's matcher already carries basePath.
    public async Task<FsResult<IReadOnlyList<string>>> GlobEntriesAsync(
        string? basePath, string pattern, CancellationToken ct)
    {
        if (!GlobScope.Create(basePath, pattern).TryGetValue(out var scope, out var scopeError))
        {
            return new FsResult<IReadOnlyList<string>>.Err(scopeError);
        }

        var items = await downloadClient.GetDownloadItems(ct);

        var dirs = items
            .Select(i => $"{MediaFilesystem.DownloadsSubdir}/{i.Id}")
            .Where(scope.Matches)
            .Select(c => c + "/");

        IEnumerable<string> files = scope.DirsOnly
            ? []
            : items
                .Select(i => $"{MediaFilesystem.DownloadsSubdir}/{i.Id}/{DownloadsPath.StatusFileName}")
                .Where(scope.Matches);

        try
        {
            return new FsResult<IReadOnlyList<string>>.Ok(
                dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList());
        }
        catch (RegexMatchTimeoutException)
        {
            return new FsResult<IReadOnlyList<string>>.Err(GlobRegex.TimedOut(pattern));
        }
    }

    // Null when the overlay owns nothing at this path: only the leftover status file of a download
    // that no longer exists, which is a real file the disk must be allowed to remove. Every other
    // path on this mount is answered here, including the refusals.
    public async Task<FsResult<FsRemoveResult>?> TryDeleteAsync(string path, CancellationToken ct)
    {
        var node = ParseNode(path);
        if (node.Kind == DownloadNodeKind.StatusFile)
        {
            return await downloadClient.GetDownloadItem(node.Id!.Value, ct) is null
                ? null
                : new FsResult<FsRemoveResult>.Err(Error(ToolError.Codes.UnsupportedOperation, $"{path} is read-only"));
        }

        if (node.Kind != DownloadNodeKind.DownloadDir)
        {
            return new FsResult<FsRemoveResult>.Err(Error(
                ToolError.Codes.UnsupportedOperation,
                $"fs_delete on the media filesystem only removes download directories ({MediaFilesystem.DownloadsSubdir}/<id>)."));
        }

        var id = node.Id!.Value;
        if (await downloadClient.GetDownloadItem(id, ct) is not null)
        {
            // Deliberately best-effort / non-transactional: a Cleanup failure throws and aborts
            // before the housekeeping steps (so we never orphan routing/files for a download
            // that is still running), while the on-disk dir removal is swallowed because
            // leftover/missing files must not undo a successful manager-side cleanup.
            await downloadClient.Cleanup(id, ct);
            await routingStore.RemoveAsync(id, ct);
            await RemoveDownloadDirectoryAsync(id, ct);
            return Removed(path, "Download cancelled and its files removed.");
        }

        if (Directory.Exists(DiskDir(id)))
        {
            // Leftover recovery: no torrent owns the id, but the directory survived a crash or
            // an external removal. Here the dir removal IS the point, so failures propagate.
            await fileSystemClient.RemoveDirectory(DiskDir(id), ct);
            await routingStore.RemoveAsync(id, ct);
            return Removed(path, "Leftover download directory removed.");
        }

        return new FsResult<FsRemoveResult>.Err(Error(ToolError.Codes.NotFound, $"Path not found: {path}"));
    }

    private async Task RemoveDownloadDirectoryAsync(int id, CancellationToken ct)
    {
        try
        {
            await fileSystemClient.RemoveDirectory(DiskDir(id), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Best-effort: a missing or undeletable directory does not undo the cleanup.
        }
    }

    private string DiskDir(int id) =>
        Path.Combine(libraryPath.BaseLibraryPath, MediaFilesystem.DownloadsSubdir, id.ToString());

    // Tools receive mount-relative paths from the agent, but the legacy disk tools also accept
    // absolute paths under the library root — normalize those before classifying.
    private DownloadsNode ParseNode(string path)
    {
        var node = DownloadsPath.Parse(path);
        return node.Kind == DownloadNodeKind.Other ? DownloadsPath.Parse(ToMountRelative(path)) : node;
    }

    // An absolute path under the library root, spelled the way the agent addresses the mount.
    // Anything else — already relative, or rooted somewhere else entirely — is left alone.
    private string ToMountRelative(string path)
    {
        if (!Path.IsPathRooted(path))
        {
            return path;
        }

        var root = Path.GetFullPath(libraryPath.BaseLibraryPath);
        var full = Path.GetFullPath(path);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSep, StringComparison.Ordinal)
            ? Path.GetRelativePath(root, full).Replace('\\', '/')
            : path;
    }

    private static string RenderStatus(DownloadItem item) => JsonSerializer.Serialize(new
    {
        id = item.Id,
        title = item.Title,
        state = item.State.ToString(),
        progressPercent = Math.Round(item.Progress * 100, 2),
        sizeMb = item.Size,
        downSpeedMbps = item.DownSpeed,
        upSpeedMbps = item.UpSpeed,
        etaMinutes = item.Eta
    }, _json);

    private static FsResult<FsRemoveResult> Removed(string path, string message) =>
        new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
        {
            Status = "removed",
            Message = message,
            OriginalPath = path,
            TrashPath = ""
        });

    private static ToolErrorResult Error(string code, string message) =>
        new() { ErrorCode = code, Message = message, Retryable = false };
}