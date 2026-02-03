using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerText.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpTextSearchTool(McpSettings settings)
    : TextSearchTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public CallToolResult McpRun(
        [Description("Text or regex pattern to search for")]
        string query,
        [Description("Treat query as regex pattern")]
        bool regex = false,
        [Description("Search within this single file only (ignores directoryPath and filePattern)")]
        string? filePath = null,
        [Description("Glob pattern to filter files (e.g., '*.md')")]
        string? filePattern = null,
        [Description("Directory to search in (default: entire vault)")]
        string directoryPath = "/",
        [Description("Maximum number of matches to return")]
        int maxResults = 50,
        [Description("Lines of context around each match")]
        int contextLines = 1,
        [Description("Output mode: 'content' for matching lines with context, 'files_only' for file paths with match counts")]
        string outputMode = "content")
    {
        return ToolResponse.Create(Run(query, regex, filePath, filePattern, directoryPath, maxResults, contextLines, outputMode));
    }
}
