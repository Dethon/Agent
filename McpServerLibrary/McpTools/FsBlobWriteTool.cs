using System.ComponentModel;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsBlobWriteTool(LibraryPathConfig libraryPath, DownloadsOverlay downloads)
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
        if (LibraryFilesystem.Reject(filesystem) is { } error)
        {
            return ToolResponse.Create(error);
        }

        if (downloads.IsVirtualPath(path))
        {
            return ToolResponse.Create(LibraryFilesystem.VirtualPathError());
        }

        return ToolResponse.Create(Run(path, contentBase64, offset, overwrite, createDirectories));
    }
}