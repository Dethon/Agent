using System.ComponentModel;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public class FsCreateTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_create")]
    [Description("Arm a timer: fs_create /<descriptive-id>/timer.json with JSON {durationSeconds, text?, target}")]
    public async Task<CallToolResult> McpRun(string path, string content, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default)
        => ToolResponse.Create(await fs.CreateAsync(path, content, overwrite, createDirectories, ct));
}