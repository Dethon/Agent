using System.ComponentModel;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public class FsInfoTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_info")]
    [Description("Get info about a timer filesystem path")]
    public async Task<CallToolResult> McpRun(string path, CancellationToken ct = default)
        => ToolResponse.Create(await fs.InfoAsync(path, ct));
}