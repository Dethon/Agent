using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerText.Settings;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpTextSearchTool(McpSettings settings, ILogger<McpTextSearchTool> logger)
    : TextSearchTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public CallToolResult McpRun(
        [Description("Text or regex pattern to search for")]
        string query,
        [Description("Treat query as regex pattern")]
        bool regex = false,
        [Description("Glob pattern to filter files (e.g., '*.md')")]
        string? filePattern = null,
        [Description("Directory to search in (default: entire vault)")]
        string path = "/",
        [Description("Maximum number of matches to return")]
        int maxResults = 50,
        [Description("Lines of context around each match")]
        int contextLines = 1)
    {
        try
        {
            return ToolResponse.Create(Run(query, regex, filePattern, path, maxResults, contextLines));
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(ex, "Error in {ToolName} tool", Name);
            }

            return ToolResponse.Create(ex);
        }
    }
}