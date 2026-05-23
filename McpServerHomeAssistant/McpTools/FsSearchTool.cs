using System.ComponentModel;
using Domain.DTOs;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class FsSearchTool(HaFileSystem fs)
{
    [McpServerTool(Name = "fs_search")]
    [Description("Searches Home Assistant entity state files (entity_id, friendly_name, attributes). Scope with directoryPath (e.g. /ha/entities/light or /ha/areas/salon) or path (a single state.yaml); omit both to search every entity. Use to find e.g. everything currently 'on'.")]
    public async Task<CallToolResult> McpRun(
        string query,
        bool regex = false,
        string? path = null,
        string? directoryPath = null,
        string? filePattern = null,
        int maxResults = 50,
        int contextLines = 1,
        string outputMode = "content",
        CancellationToken cancellationToken = default)
    {
        var searchOutputMode = outputMode.Equals("filesOnly", StringComparison.OrdinalIgnoreCase)
            ? VfsTextSearchOutputMode.FilesOnly
            : VfsTextSearchOutputMode.Content;
        return ToolResponse.Create(
            await fs.SearchAsync(
                query, regex, path, directoryPath, filePattern, maxResults, contextLines, searchOutputMode, cancellationToken));
    }
}