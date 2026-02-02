using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerText.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpTextEditTool(McpSettings settings)
    : TextEditTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public CallToolResult McpRun(
        [Description("Path to the text file (absolute or relative to vault)")]
        string filePath,
        [Description("Exact text to find (case-sensitive)")]
        string oldString,
        [Description("Replacement text")]
        string newString,
        [Description("Replace all occurrences (default: false)")]
        bool replaceAll = false)
    {
        return ToolResponse.Create(Run(filePath, oldString, newString, replaceAll));
    }
}
