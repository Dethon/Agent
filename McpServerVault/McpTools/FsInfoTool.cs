using System.ComponentModel;
using Domain.Tools.Files;
using Infrastructure.Utils;
using McpServerVault.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerVault.McpTools;

[McpServerToolType]
public class FsInfoTool(McpSettings settings) : FileInfoTool(settings.VaultPath)
{
    [McpServerTool(Name = "fs_info")]
    [Description(Description)]
    public CallToolResult McpRun(string path)
    {
        return ToolResponse.Create(Run(path));
    }
}
