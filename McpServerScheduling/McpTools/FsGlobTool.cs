using System.ComponentModel;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public class FsGlobTool(ScheduleFileSystem fs)
{
    [McpServerTool(Name = "fs_glob")]
    [Description("List schedule filesystem entries matching a glob under basePath")]
    public async Task<CallToolResult> McpRun(string pattern, string basePath = "/", CancellationToken ct = default)
        => ToolResponse.Create(await fs.GlobAsync(basePath, pattern, ct));
}