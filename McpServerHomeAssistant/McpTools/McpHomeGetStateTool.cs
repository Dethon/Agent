using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.HomeAssistant;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class McpHomeGetStateTool(IHomeAssistantClient client) : HomeGetStateTool(client)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        [Description("Entity ID, e.g. 'vacuum.roborock_s8'")] string entityId,
        CancellationToken ct = default)
    {
        return ToolResponse.Create(await RunAsync(entityId, ct));
    }
}