using System.ComponentModel;
using Domain.Tools.Files;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class FsGlobTool(HaFileSystem fs)
{
    [McpServerTool(Name = "fs_glob")]
    [Description("Lists Home Assistant entities, areas, and action files matching a glob pattern. Use mode 'directories' to explore (domains, entities, areas), 'files' to find state.yaml and *.sh action files.")]
    public async Task<CallToolResult> McpRun(
        string pattern,
        string mode = "directories",
        string basePath = "",
        CancellationToken cancellationToken = default)
    {
        var globMode = mode.Equals("files", StringComparison.OrdinalIgnoreCase) ? GlobMode.Files : GlobMode.Directories;
        return ToolResponse.Create(await fs.GlobAsync(basePath, pattern, globMode, cancellationToken));
    }
}