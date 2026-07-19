using System.ComponentModel;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public class FsMoveTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_move")]
    [Description("Unsupported on timers — kept for VFS surface completeness")]
    public async Task<CallToolResult> McpRun(string sourcePath, string destinationPath, CancellationToken ct = default)
        => ToolResponse.Create(await fs.MoveAsync(sourcePath, destinationPath, ct));
}