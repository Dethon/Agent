using System.ComponentModel;
using Domain.DTOs;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public class FsEditTool(ScheduleFileSystem fs)
{
    [McpServerTool(Name = "fs_edit")]
    [Description("Edit a schedule.json (prompt/timing/deliverTo)")]
    public async Task<CallToolResult> McpRun(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct = default)
        => ToolResponse.Create(await fs.EditAsync(path, edits, ct));
}