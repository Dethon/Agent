using System.ComponentModel;
using Domain.Tools;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsCopyTool(LibraryPathConfig libraryPath, DownloadsFileSystem downloads)
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
        if (filesystem == downloads.FilesystemName)
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                "The downloads filesystem does not support this operation.",
                retryable: false));
        }

        return ToolResponse.Create(Run(sourcePath, destinationPath, overwrite, createDirectories));
    }
}