using System.ComponentModel;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsCopyTool(LibraryPathConfig libraryPath, DownloadsOverlay downloads)
    : CopyTool(libraryPath.BaseLibraryPath)
{
    [McpServerTool(Name = "fs_copy")]
    [Description(Description)]
    public CallToolResult McpRun(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        bool createDirectories = true,
        string? filesystem = null)
    {
        if (LibraryFilesystem.Reject(filesystem) is { } error)
        {
            return ToolResponse.Create(error);
        }

        if (downloads.IsVirtualPath(sourcePath) || downloads.IsVirtualPath(destinationPath))
        {
            return ToolResponse.Create(LibraryFilesystem.VirtualPathError());
        }

        return ToolResponse.Create(Run(sourcePath, destinationPath, overwrite, createDirectories));
    }
}