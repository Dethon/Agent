using System.ComponentModel;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class FsInfoTool(HaFileSystem fs)
{
    [McpServerTool(Name = "fs_info")]
    [Description("Returns metadata for a Home Assistant virtual path: exists, isDirectory. Cheap existence check before read/exec.")]
    public async Task<CallToolResult> McpRun(string path, CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(await fs.InfoAsync(path, cancellationToken));
    }
}