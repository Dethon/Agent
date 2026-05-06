using System.ComponentModel;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsBlobWriteTool(LibraryPathConfig libraryPath) : BlobWriteTool(libraryPath.BaseLibraryPath)
{
    [McpServerTool(Name = "fs_blob_write")]
    [Description(Description)]
    public CallToolResult McpRun(
        string path,
        string contentBase64,
        long offset = 0,
        bool overwrite = false,
        bool createDirectories = true)
    {
        return ToolResponse.Create(Run(path, contentBase64, offset, overwrite, createDirectories));
    }
}
