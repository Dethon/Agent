using System.ComponentModel;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class FsGlobTool(HaFileSystem fs)
{
    [McpServerTool(Name = "fs_glob")]
    [Description("Lists Home Assistant entities, areas, and action files matching a glob pattern. "
        + "`*` matches one path segment, `**` recurses. A trailing slash lists directories only "
        + "(domains, entities, areas — e.g. `*/`); otherwise files (`state.json`, `*.sh`) and "
        + "directories both match, with directories returned with a trailing slash.")]
    public async Task<CallToolResult> McpRun(
        string pattern,
        string basePath = "",
        CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(await fs.GlobAsync(basePath, pattern, cancellationToken));
    }
}