using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Commands;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerCommandRunner.McpTools;

[McpServerToolType]
public class McpRunCommandTool(
    ICommandRunner commandRunner) : RunCommandTool(commandRunner)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(string command, CancellationToken cancellationToken)
    {
        return ToolResponse.Create(await Run(command, cancellationToken));
    }
}
