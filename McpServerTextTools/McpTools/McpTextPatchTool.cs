using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerTextTools.Settings;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerTextTools.McpTools;

[McpServerToolType]
public class McpTextPatchTool(McpSettings settings, ILogger<McpTextPatchTool> logger)
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
        try
        {
            var targetObj = JsonNode.Parse(target)?.AsObject() ??
                            throw new ArgumentException("Target must be a valid JSON object");

            return ToolResponse.Create(Run(filePath, operation, targetObj, content, preserveIndent));
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Invalid JSON in target parameter");
            return ToolResponse.Create(new ArgumentException($"Invalid JSON in target: {ex.Message}"));
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