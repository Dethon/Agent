using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerVault.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerVault.McpTools;

[McpServerToolType]
public class FsEditTool(McpSettings settings)
    : TextEditTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = "fs_edit")]
    [Description("Edit file via exact string replacement")]
    public CallToolResult McpRun(
        string filesystem,
        string path,
        string oldString,
        string newString,
        bool replaceAll = false)
    {
        return ToolResponse.Create(Run(path, oldString, newString, replaceAll));
    }
}
