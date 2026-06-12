using System.ComponentModel;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsInfoTool(LibraryPathConfig libraryPath, DownloadsFileSystem downloads)
    : FileInfoTool(libraryPath.BaseLibraryPath)
{
    [McpServerTool(Name = "fs_info")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string path,
        string? filesystem = null,
        CancellationToken ct = default)
        => filesystem == downloads.FilesystemName
            ? ToolResponse.Create(await downloads.InfoAsync(path, ct))
            : ToolResponse.Create(Run(path));
}