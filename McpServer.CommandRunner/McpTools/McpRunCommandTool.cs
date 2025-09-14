using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServer.CommandRunner.McpTools;

[McpServerToolType]
public class McpRunCommandTool(
    ICommandRunner commandRunner,
    ILogger<McpRunCommandTool> logger) : RunCommandTool(commandRunner)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(string command, CancellationToken cancellationToken)
    {
        try
        {
            return ToolResponse.Create(await Run(command, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in {ToolName} tool", Name);
            return ToolResponse.Create(ex);
        }
    }
}