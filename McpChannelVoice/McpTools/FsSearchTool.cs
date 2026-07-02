using System.ComponentModel;
using Domain.DTOs;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public class FsSearchTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_search")]
    [Description("Searches timer.json content across timers. Scope with path (a single timer); omit to search every timer. Supports regex, filePattern, maxResults, contextLines, and outputMode (content|filesOnly) like the other filesystems.")]
    public async Task<CallToolResult> McpRun(
        string query, bool regex = false, string? path = null, string? directoryPath = null,
        string? filePattern = null, int maxResults = 50, int contextLines = 1, string outputMode = "content",
        CancellationToken ct = default)
    {
        var searchOutputMode = outputMode.Equals("filesOnly", StringComparison.OrdinalIgnoreCase)
            ? VfsTextSearchOutputMode.FilesOnly
            : VfsTextSearchOutputMode.Content;
        return ToolResponse.Create(await fs.SearchAsync(
            query, regex, path, directoryPath, filePattern, maxResults, contextLines, searchOutputMode, ct));
    }
}