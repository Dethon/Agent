using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerText.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpTextReadTool(McpSettings settings)
    : TextReadTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public CallToolResult McpRun(
        [Description("Path to the text file (absolute or relative to vault)")]
        string filePath,
        [Description(
            "Target specification as JSON. Use ONE of: lines {start,end}, heading {text,includeChildren}, codeBlock {index}, anchor, section, search {query,contextLines}")]
        string target)
    {
        var targetObj = JsonNode.Parse(target)?.AsObject()
                        ?? throw new ArgumentException("Target must be a valid JSON object");

        return ToolResponse.Create(Run(filePath, targetObj));
    }
}
