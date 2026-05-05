using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerSandbox.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsReadTool(McpSettings settings)
    : TextReadTool(settings.ContainerRoot, settings.AllowedExtensions)
{
    [McpServerTool(Name = "fs_read")]
    [Description(Description)]
    public CallToolResult McpRun(
        string path,
        int? offset = null,
        int? limit = null)
    {
        return ToolResponse.Create(Run(path, offset, limit));
    }
}
