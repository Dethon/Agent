using System.ComponentModel;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public class FsDeleteTool(ScheduleFileSystem fs)
{
    [McpServerTool(Name = "fs_delete")]
    [Description("Delete a schedule directory")]
    public async Task<CallToolResult> McpRun(string path, CancellationToken ct = default)
        => ToolResponse.Create(await fs.DeleteAsync(path, ct));
}