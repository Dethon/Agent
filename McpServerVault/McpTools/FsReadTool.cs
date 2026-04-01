using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerVault.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerVault.McpTools;

[McpServerToolType]
public class FsReadTool(McpSettings settings)
    : TextReadTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = "fs_read")]
    [Description("Read file content with optional pagination")]
    public CallToolResult McpRun(
        string filesystem,
        string path,
        int? offset = null,
        int? limit = null)
    {
        return ToolResponse.Create(Run(path, offset, limit));
    }
}
