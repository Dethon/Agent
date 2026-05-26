using System.ComponentModel;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public class FsMoveTool(ScheduleFileSystem fs)
{
    [McpServerTool(Name = "fs_move")]
    [Description("Reassign a schedule to another agent or rename it: move /<agent>/<id> to /<agent2>/<id2>")]
    public async Task<CallToolResult> McpRun(string sourcePath, string destinationPath, CancellationToken ct = default)
        => ToolResponse.Create(await fs.MoveAsync(sourcePath, destinationPath, ct));
}