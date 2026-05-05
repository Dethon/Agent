using System.ComponentModel;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsBlobReadTool(LibraryPathConfig libraryPath) : BlobReadTool(libraryPath.BaseLibraryPath)
{
    [McpServerTool(Name = "fs_blob_read")]
    [Description(Description)]
    public CallToolResult McpRun(
        string path,
        long offset = 0,
        int length = MaxChunkSizeBytes)
    {
        return ToolResponse.Create(Run(path, offset, length));
    }
}
