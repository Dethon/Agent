using System.ComponentModel;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public class FsDeleteTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_delete")]
    [Description("Cancel a timer by deleting its directory /<timerId>")]
    public async Task<CallToolResult> McpRun(string path, CancellationToken ct = default)
        => ToolResponse.Create(await fs.DeleteAsync(path, ct));
}