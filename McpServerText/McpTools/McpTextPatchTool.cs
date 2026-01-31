using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerText.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpTextPatchTool(McpSettings settings)
    : TextPatchTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public CallToolResult McpRun(
        [Description("Path to the text file (absolute or relative to vault)")]
        string filePath,
        [Description("Operation: 'replace', 'insert', 'delete', or 'replaceLines'")]
        string operation,
        [Description(
            "Target specification as JSON. Use ONE of: lines {start,end}, text, pattern, heading, afterHeading, beforeHeading, codeBlock {index}, section")]
        string target,
        [Description("New content for replace/insert operations")]
        string? content = null,
        [Description("Match indentation of target line (default: true)")]
        bool preserveIndent = true)
    {
        var targetObj = JsonNode.Parse(target)?.AsObject() ??
                        throw new ArgumentException("Target must be a valid JSON object");

        return ToolResponse.Create(Run(filePath, operation, targetObj, content, preserveIndent));
    }
}
