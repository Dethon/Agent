using System.ComponentModel;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public class FsCreateTool(ScheduleFileSystem fs)
{
    [McpServerTool(Name = "fs_create")]
    [Description("Create a schedule: fs_create /<agentId>/<descriptive-id>/schedule.json with JSON {prompt, cron|runAt, userId?, deliverTo?}")]
    public async Task<CallToolResult> McpRun(string path, string content, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default)
        => ToolResponse.Create(await fs.CreateAsync(path, content, overwrite, createDirectories, ct));
}