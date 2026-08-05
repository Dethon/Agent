using System.Text.Json;
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

    // True when this path and a live download's directory overlap: the directory itself, any
    // ancestor of it (moving a parent takes the directory with it), and anything under it (the
    // payload files the download is still writing). Deleting such a path is the documented cancel,
    // but moving it is not — on the way out the download keeps writing and recreates what it lost,
    // leaving the moved copy orphaned, and on the way in whatever landed inside is destroyed the
    // moment the download is cancelled.
    public async Task<bool> TouchesActiveDownloadAsync(string path, CancellationToken ct)
    {
        var candidate = ToMountRelative(path).Trim('/');
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
            return new FsResult<FsReadResult>.Err(Error(ToolError.Codes.NotFound, $"Path not found: {path}"));
        }

        var content = RenderStatus(item);
        return new FsResult<FsReadResult>.Ok(new FsReadResult
        {
            FilePath = path,
            Content = content,
            TotalLines = content.Split('\n').Length,
            Truncated = false
        });
    }

    public async Task<FsResult<FsInfoResult>?> TryInfoAsync(string path, CancellationToken ct)
    {
        var node = ParseNode(path);
        switch (node.Kind)
        {
            case DownloadNodeKind.StatusFile:
                {
                    var item = await downloadClient.GetDownloadItem(node.Id!.Value, ct);
                    return new FsResult<FsInfoResult>.Ok(new FsInfoResult
                    {
                        Exists = item is not null,
                        Path = path,
                        IsDirectory = item is not null ? false : null,
                        Size = item is not null ? RenderStatus(item).Length : null
                    });
                }
            case DownloadNodeKind.DownloadDir when await downloadClient.GetDownloadItem(node.Id!.Value, ct) is not null:
                return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = path, IsDirectory = true });
            default:
                return null;
        }
    }

    public async Task<IReadOnlyList<string>> GlobEntriesAsync(string? basePath, string pattern, CancellationToken ct)
    {
        var prefix = (basePath ?? "").Trim('/');
        var items = await downloadClient.GetDownloadItems(ct);

        var dirsOnly = pattern.EndsWith('/');
        var effectivePattern = dirsOnly ? pattern.TrimEnd('/') : pattern;
        var matches = GlobRegex.CompileMatcher(effectivePattern);

        // Candidates are library-root-relative (same convention as the disk glob results);
        // the pattern applies relative to basePath, mirroring the disk matcher root.
        string? relative(string candidate) =>
            prefix.Length == 0 ? candidate
            : candidate.StartsWith(prefix + "/", StringComparison.Ordinal) ? candidate[(prefix.Length + 1)..]
            : null;

        var dirs = items
            .Select(i => $"{MediaFilesystem.DownloadsSubdir}/{i.Id}")
            .Where(c => relative(c) is { } rel && matches(rel))
            .Select(c => c + "/");

        if (dirsOnly)
        {
            return dirs.OrderBy(p => p, StringComparer.Ordinal).ToList();
        }

        var files = items
            .Select(i => $"{MediaFilesystem.DownloadsSubdir}/{i.Id}/{DownloadsPath.StatusFileName}")
            .Where(c => relative(c) is { } rel && matches(rel));

        return dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    public async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        var node = ParseNode(path);
        if (node.Kind == DownloadNodeKind.StatusFile)
        {
            return new FsResult<FsRemoveResult>.Err(Error(ToolError.Codes.UnsupportedOperation, $"{path} is read-only"));
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