using System.ComponentModel;
using Domain.Tools.Files;
using Infrastructure.Utils;
using McpServerVault.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerVault.McpTools;

[McpServerToolType]
public class FsBlobReadTool(McpSettings settings) : BlobReadTool(settings.VaultPath)
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