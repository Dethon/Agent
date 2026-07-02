using System.ComponentModel;
using Domain.DTOs;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public class FsEditTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_edit")]
    [Description("Unsupported on timers (immutable) — kept for VFS surface completeness")]
    public async Task<CallToolResult> McpRun(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct = default)
        => ToolResponse.Create(await fs.EditAsync(path, edits, ct));
}