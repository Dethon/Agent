using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerText.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpTextReplaceTool(McpSettings settings)
    : TextReplaceTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public CallToolResult McpRun(
        [Description("Path to the text file (absolute or relative to vault)")]
        string filePath,
        [Description("Exact text to find (case-sensitive)")]
        string oldText,
        [Description("Replacement text")]
        string newText,
        [Description("Which occurrence to replace: 'first' (default), 'last', 'all', or numeric index (1-based)")]
        string occurrence = "first",
        [Description("Optional 16-character file hash for validation to detect conflicts")]
        string? expectedHash = null)
    {
        return ToolResponse.Create(Run(filePath, oldText, newText, occurrence, expectedHash));
    }
}
