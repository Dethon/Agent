using System.ComponentModel;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public class FsExecTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_exec")]
    [Description("Unsupported on timers — kept for VFS surface completeness")]
    public async Task<CallToolResult> McpRun(string path, string command, int? timeoutSeconds = null, CancellationToken ct = default)
        => ToolResponse.Create(await fs.ExecAsync(path, command, timeoutSeconds, ct));
}