using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerCommandRunner.McpTools;

[McpServerToolType]
public class McpGetCliPlatformTool(
    IAvailableShell shell,
    ILogger<McpGetCliPlatformTool> logger) : GetCliPlatformTool(shell)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(CancellationToken ct)
    {
        try
        {
            return ToolResponse.Create(await Run(ct));
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