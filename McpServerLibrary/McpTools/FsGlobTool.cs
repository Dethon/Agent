using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsGlobTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath,
    DownloadsOverlay downloads) : GlobFilesTool(client, libraryPath)
{
    [McpServerTool(Name = "fs_glob")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string pattern,
        string basePath = "",
        string? filesystem = null,
        CancellationToken cancellationToken = default)
    {
        if (LibraryFilesystem.Reject(filesystem) is { } error)
        {
            return ToolResponse.Create(error);
        }

        var disk = await RunCore(pattern, cancellationToken, basePath);
        var virtualEntries = await downloads.GlobEntriesAsync(basePath, pattern, cancellationToken);
        return ToolResponse.Create(FsResultContract.ToNode(Merge(disk, virtualEntries)));
    }

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
            Entries = combined.Take(FileResultCap).ToList(),
            Truncated = disk.Truncated || combined.Count > FileResultCap,
            Total = disk.Total + added.Count
        };
    }
}