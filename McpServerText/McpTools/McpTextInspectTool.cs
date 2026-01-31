using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerText.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpTextInspectTool(McpSettings settings)
    : TextInspectTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public CallToolResult McpRun(
        [Description("Path to the text file (absolute or relative to vault)")]
        string filePath,
        [Description("Inspection mode: 'structure' (default), 'search', or 'lines'")]
        string mode = "structure",
        [Description("Search pattern for 'search' mode, or line range for 'lines' mode (e.g., '50-60')")]
        string? query = null,
        [Description("Treat query as regex pattern (default: false)")]
        bool regex = false,
        [Description("Lines of context around search matches (default: 0)")]
        int context = 0)
    {
        return ToolResponse.Create(Run(filePath, mode, query, regex, context));
    }
}
