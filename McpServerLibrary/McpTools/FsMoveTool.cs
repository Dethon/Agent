using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools;
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
    DownloadsFileSystem downloads) : MoveTool(client, libraryPath)
{
    [McpServerTool(Name = "fs_move")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string sourcePath,
        string destinationPath,
        string? filesystem = null,
        CancellationToken cancellationToken = default)
    {
        if (filesystem == downloads.FilesystemName)
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                "The downloads filesystem does not support this operation.",
                retryable: false));
        }

        return ToolResponse.Create(await Run(sourcePath, destinationPath, cancellationToken));
    }
}