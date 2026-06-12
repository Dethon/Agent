using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Downloads.Vfs;

// Exposes the download manager as a read-only virtual filesystem: /downloads/<id>/status.json reports
// a download's live state, and fs_delete /downloads/<id> cancels the download and cleans up its files.
// Downloads are started by the download_file tool, not by creating files here.
public sealed class DownloadsFileSystem(
    IDownloadClient downloadClient,
    IDownloadRoutingStore routingStore,
    IFileSystemClient fileSystemClient,
    DownloadPathConfig pathConfig) : IFileSystemBackend
{
    public string FilesystemName => "downloads";

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        var node = DownloadsPath.Parse(path);
        if (node.Kind == DownloadNodeKind.DownloadDir)
        {
            return Unsupported<FsReadResult>($"{path} is a directory; read /downloads/<id>/status.json instead.");
        }

        if (node.Kind != DownloadNodeKind.StatusFile)
        {
            return NotFound<FsReadResult>(path);
        }

        var item = await downloadClient.GetDownloadItem(node.Id!.Value, ct);
        if (item is null)
        {
            return NotFound<FsReadResult>(path);
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

    public async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        var node = DownloadsPath.Parse(path);
        switch (node.Kind)
        {
            case DownloadNodeKind.Root:
                return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = path, IsDirectory = true });
            case DownloadNodeKind.DownloadDir:
                {
                    var exists = await downloadClient.GetDownloadItem(node.Id!.Value, ct) is not null;
                    return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = exists, Path = path, IsDirectory = exists ? true : null });
                }
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
            default:
                return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = false, Path = path });
        }
    }

    public async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        var items = await downloadClient.GetDownloadItems(ct);
        var prefix = string.IsNullOrEmpty(basePath?.Trim('/')) ? string.Empty : basePath.Trim('/') + "/";

        var dirsOnly = pattern.EndsWith('/');
        var effectivePattern = dirsOnly ? pattern.TrimEnd('/') : pattern;
        var matches = GlobRegex.CompileMatcher(prefix + effectivePattern);

        var dirs = items.Select(i => i.Id.ToString()).Where(matches).Select(id => $"/{id}/");
        if (dirsOnly)
        {
            return Glob(dirs.OrderBy(p => p, StringComparer.Ordinal).ToList());
        }

        var files = items.Select(i => $"{i.Id}/{DownloadsPath.StatusFileName}").Where(matches).Select(p => $"/{p}");
        return Glob(dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList());
    }

    public async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        var node = DownloadsPath.Parse(path);
        if (node.Kind == DownloadNodeKind.StatusFile)
        {
            return ReadOnly<FsRemoveResult>(path);
        }

        if (node.Kind != DownloadNodeKind.DownloadDir)
        {
            return NotFound<FsRemoveResult>(path);
        }

        var id = node.Id!.Value;
        if (await downloadClient.GetDownloadItem(id, ct) is null)
        {
            return NotFound<FsRemoveResult>(path);
        }

        // Deliberately best-effort / non-transactional, mirroring PrinterQueueFileSystem: a Cleanup failure
        // throws and aborts before the housekeeping steps (so we never orphan routing/files for a download
        // that is still running), while the on-disk dir removal is swallowed because leftover/missing files
        // must not undo a successful manager-side cleanup.
        await downloadClient.Cleanup(id, ct);
        await routingStore.RemoveAsync(id, ct);
        await RemoveDownloadDirectoryAsync(id, ct);

        return new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
        {
            Status = "removed",
            Message = "Download cancelled and its files removed.",
            OriginalPath = path,
            TrashPath = ""
        });
    }

    // Leftover or already-missing download directories are non-fatal: the manager-side cleanup is the
    // crux, so a failed/absent on-disk removal must not fail the delete.
    private async Task RemoveDownloadDirectoryAsync(int id, CancellationToken ct)
    {
        try
        {
            await fileSystemClient.RemoveDirectory(Path.Combine(pathConfig.BaseDownloadPath, id.ToString()), ct);
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

    public Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsCreateResult>("The downloads filesystem is read-only. Use the download_file tool to start a download."));

    public Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsEditResult>("The downloads filesystem is read-only. Use the download_file tool to start a download."));

    public Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsMoveResult>("The downloads filesystem does not support move."));

    public Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath, bool overwrite, bool createDirectories, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsCopyResult>("The downloads filesystem does not support copy."));

    public Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsExecResult>("The downloads filesystem does not support exec."));

    public Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path, string? directoryPath, string? filePattern,
        int maxResults, int contextLines, VfsTextSearchOutputMode outputMode, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsSearchResult>("The downloads filesystem does not support search. Read /downloads/<id>/status.json directly."));

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(string path, CancellationToken ct) =>
        throw new NotSupportedException("The downloads filesystem does not support raw byte streaming.");

    public Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        throw new NotSupportedException("The downloads filesystem does not support raw byte streaming.");

    private static string RenderStatus(DownloadItem item) => JsonSerializer.Serialize(new
    {
        id = item.Id,
        title = item.Title,
        state = item.State.ToString(),
        progressPercent = Math.Round(item.Progress * 100, 2),
        sizeMb = item.Size,
        downSpeedMbps = item.DownSpeed,
        upSpeedMbps = item.UpSpeed,
        etaMinutes = item.Eta,
        savePath = item.SavePath
    }, _json);

    private static FsResult<FsGlobResult> Glob(IReadOnlyList<string> entries) => new FsResult<FsGlobResult>.Ok(new FsGlobResult
    {
        Entries = entries,
        Truncated = false,
        Total = entries.Count
    });

    private static FsResult<T> ReadOnly<T>(string path) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.UnsupportedOperation, $"{path} is read-only"));

    private static FsResult<T> NotFound<T>(string path) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.NotFound, $"Path not found: {path}"));

    private static FsResult<T> Unsupported<T>(string message) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.UnsupportedOperation, message));

    private static ToolErrorResult Error(string code, string message) =>
        new() { ErrorCode = code, Message = message, Retryable = false };
}