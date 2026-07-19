using System.ComponentModel;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public class FsGlobTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_glob")]
    [Description("Lists timer filesystem entries matching a glob under basePath. `*` matches one "
        + "path segment, `**` recurses, `?` one char, `{a,b}` brace alternation. A trailing slash "
        + "(e.g. `*/`) lists directories only; otherwise files and directories both match, with "
        + "directory results marked by a trailing slash.")]
    public async Task<CallToolResult> McpRun(string pattern, string basePath = "/", CancellationToken ct = default)
        => ToolResponse.Create(await fs.GlobAsync(basePath, pattern, ct));
}