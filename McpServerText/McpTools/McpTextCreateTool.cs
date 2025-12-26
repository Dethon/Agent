using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerText.Settings;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpTextCreateTool(McpSettings settings, ILogger<McpTextCreateTool> logger)
    : TextCreateTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public CallToolResult McpRun(
        [Description("Path for the new file (relative to vault or absolute)")]
        string filePath,
        [Description("Initial content for the file")]
        string content,
        [Description("Create parent directories if they don't exist")]
        bool createDirectories = true)
    {
        try
        {
            return ToolResponse.Create(Run(filePath, content, createDirectories));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in {ToolName} tool", Name);
            return ToolResponse.Create(ex);
        }
    }
}