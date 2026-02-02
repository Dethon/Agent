using System.ComponentModel;
using System.Text.Json.Nodes;
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
        [Description("Operation: 'replace', 'insert', or 'delete'. Text targets only support 'replace'.")]
        string operation,
        [Description(
            "Target specification as JSON. Use ONE of: lines {start,end}, heading, beforeHeading, appendToSection, codeBlock {index}, text \"exact match\"")]
        string target,
        [Description("New content for replace/insert operations. For text targets, this is the replacement text.")]
        string? content = null,
        [Description(
            "For text targets only: which occurrence to replace. 'first' (default), 'last', 'all', or numeric 1-based index.")]
        string? occurrence = null,
        [Description("Match indentation of target line (default: true). Only applies to positional targets.")]
        bool preserveIndent = true,
        [Description("Expected file hash for staleness detection. Get from TextInspect.")]
        string? expectedHash = null)
    {
        var targetObj = JsonNode.Parse(target)?.AsObject() ??
                        throw new ArgumentException("Target must be a valid JSON object");

        return ToolResponse.Create(Run(filePath, operation, targetObj, content, occurrence, preserveIndent,
            expectedHash));
    }
}
