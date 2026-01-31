using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Commands;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerCommandRunner.McpTools;

[McpServerToolType]
public class McpGetCliPlatformTool(
    IAvailableShell shell) : GetCliPlatformTool(shell)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(CancellationToken ct)
    {
        return ToolResponse.Create(await Run(ct));
    }
}
