using System.ComponentModel;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsBlobReadTool(LibraryPathConfig libraryPath, DownloadsOverlay downloads)
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
        if (LibraryFilesystem.Reject(filesystem) is { } error)
        {
            return ToolResponse.Create(error);
        }

        if (downloads.IsVirtualPath(path))
        {
            return ToolResponse.Create(LibraryFilesystem.VirtualPathError());
        }

        return ToolResponse.Create(Run(path, offset, length));
    }
}