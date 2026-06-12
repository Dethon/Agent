using System.ComponentModel;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsInfoTool(LibraryPathConfig libraryPath, DownloadsOverlay downloads)
    : FileInfoTool(libraryPath.BaseLibraryPath)
{
    [McpServerTool(Name = "fs_info")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string path,
        string? filesystem = null,
        CancellationToken ct = default)
    {
        if (LibraryFilesystem.Reject(filesystem) is { } error)
        {
            return ToolResponse.Create(error);
        }

        var overlay = await downloads.TryInfoAsync(path, ct);
        return overlay is not null
            ? ToolResponse.Create(overlay)
            : ToolResponse.Create(Run(path));
    }
}