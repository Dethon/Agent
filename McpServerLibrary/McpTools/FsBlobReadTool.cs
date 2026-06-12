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
public class FsBlobReadTool(LibraryPathConfig libraryPath, DownloadsFileSystem downloads)
    : BlobReadTool(libraryPath.BaseLibraryPath)
{
    [McpServerTool(Name = "fs_blob_read")]
    [Description(Description)]
    public CallToolResult McpRun(
        string path,
        long offset = 0,
        int length = MaxChunkSizeBytes,
        string? filesystem = null)
    {
        if (filesystem == downloads.FilesystemName)
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                "The downloads filesystem does not support this operation.",
                retryable: false));
        }

        return ToolResponse.Create(Run(path, offset, length));
    }
}