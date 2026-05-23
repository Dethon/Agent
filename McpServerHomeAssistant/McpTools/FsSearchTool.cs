using System.ComponentModel;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class FsSearchTool(HaFileSystem fs)
{
    [McpServerTool(Name = "fs_search")]
    [Description("Searches across Home Assistant entity states (entity_id, friendly_name, attributes). Use to find e.g. everything currently 'on'.")]
    public async Task<CallToolResult> McpRun(
        string query,
        bool regex = false,
        string? path = null,
        string? directoryPath = null,
        string? filePattern = null,
        int maxResults = 50,
        int contextLines = 1,
        CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(
            await fs.SearchAsync(query, regex, path, directoryPath, filePattern, maxResults, contextLines, cancellationToken));
    }
}