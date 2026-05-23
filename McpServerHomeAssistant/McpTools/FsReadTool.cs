using System.ComponentModel;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class FsReadTool(HaFileSystem fs)
{
    [McpServerTool(Name = "fs_read")]
    [Description("Reads a Home Assistant virtual file: state.yaml returns the entity's live state + attributes; a *.sh file returns its usage (same as --help).")]
    public async Task<CallToolResult> McpRun(
        string path,
        int? offset = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(await fs.ReadAsync(path, offset, limit, cancellationToken));
    }
}