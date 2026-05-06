using System.ComponentModel;
using Domain.Tools.Files;
using Infrastructure.Utils;
using McpServerVault.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerVault.McpTools;

[McpServerToolType]
public class FsCopyTool(McpSettings settings) : CopyTool(settings.VaultPath)
{
    [McpServerTool(Name = "fs_copy")]
    [Description(Description)]
    public CallToolResult McpRun(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        bool createDirectories = true)
    {
        return ToolResponse.Create(Run(sourcePath, destinationPath, overwrite, createDirectories));
    }
}
