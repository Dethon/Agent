using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerVault.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerVault.McpTools;

[McpServerToolType]
public class FsSearchTool(McpSettings settings)
    : TextSearchTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = "fs_search")]
    [Description("Search file contents with text or regex")]
    public CallToolResult McpRun(
        string filesystem,
        string query,
        bool regex = false,
        string? path = null,
        string? directoryPath = null,
        string? filePattern = null,
        int maxResults = 50,
        int contextLines = 1,
        string outputMode = "content")
    {
        var searchOutputMode = outputMode.Equals("filesOnly", StringComparison.OrdinalIgnoreCase)
            ? SearchOutputMode.FilesOnly
            : SearchOutputMode.Content;

        var effectiveDirectoryPath = directoryPath ?? "/";

        return ToolResponse.Create(Run(query, regex, path, filePattern, effectiveDirectoryPath, maxResults, contextLines, searchOutputMode));
    }
}
