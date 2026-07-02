using System.ComponentModel;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public class FsReadTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_read")]
    [Description("Read a timer filesystem file (timer.json/status.json)")]
    public async Task<CallToolResult> McpRun(string path, int? offset = null, int? limit = null, CancellationToken ct = default)
        => ToolResponse.Create(await fs.ReadAsync(path, offset, limit, ct));
}