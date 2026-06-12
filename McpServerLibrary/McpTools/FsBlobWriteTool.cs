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
public class FsBlobWriteTool(LibraryPathConfig libraryPath, DownloadsFileSystem downloads)
    : BlobWriteTool(libraryPath.BaseLibraryPath)
{
    [McpServerTool(Name = "fs_blob_write")]
    [Description(Description)]
    public CallToolResult McpRun(
        string path,
        string contentBase64,
        long offset = 0,
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

        return ToolResponse.Create(Run(path, contentBase64, offset, overwrite, createDirectories));
    }
}