using System.ComponentModel;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public class FsSearchTool(ScheduleFileSystem fs)
{
    [McpServerTool(Name = "fs_search")]
    [Description("Search schedules by id, prompt, or agent. Only 'query' is evaluated; the other parameters are accepted for protocol uniformity but ignored.")]
    public async Task<CallToolResult> McpRun(
        string query, bool regex = false, string? path = null, string? directoryPath = null,
        string? filePattern = null, int maxResults = 50, int contextLines = 1, string outputMode = "content",
        CancellationToken ct = default)
        => ToolResponse.Create(await fs.SearchAsync(query, ct));
}