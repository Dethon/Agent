using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerVault.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerVault.McpTools;

[McpServerToolType]
public class FsCreateTool(McpSettings settings)
    : TextCreateTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = "fs_create")]
    [Description(Description)]
    public CallToolResult McpRun(
        string filesystem,
        string path,
        string content,
        bool overwrite = false,
        bool createDirectories = true)
    {
        return ToolResponse.Create(Run(path, content, overwrite, createDirectories));
    }
}
