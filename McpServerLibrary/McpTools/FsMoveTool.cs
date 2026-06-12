using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsMoveTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath,
    DownloadsOverlay downloads) : MoveTool(client, libraryPath)
{
    [McpServerTool(Name = "fs_move")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string sourcePath,
        string destinationPath,
        string? filesystem = null,
        CancellationToken cancellationToken = default)
    {
        if (LibraryFilesystem.Reject(filesystem) is { } error)
        {
            return ToolResponse.Create(error);
        }

        if (downloads.IsVirtualPath(sourcePath) || downloads.IsVirtualPath(destinationPath))
        {
            return ToolResponse.Create(LibraryFilesystem.VirtualPathError());
        }

        return ToolResponse.Create(await Run(sourcePath, destinationPath, cancellationToken));
    }
}