using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.HomeAssistant;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class McpHomeListEntitiesTool(IHomeAssistantClient client) : HomeListEntitiesTool(client)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        [Description("Optional domain filter (e.g. 'vacuum', 'light'). Filtering by the device's class domain hides neighboring sensors/selects/buttons/events that often carry the room, zone, mode, or ID metadata you need to control it — prefer leaving this unset or using `area` until you've seen the full picture.")] string? domain = null,
        [Description("Optional substring matched case-insensitively against friendly_name. Useful for grouping all entities belonging to one device (e.g. 'Roborock' returns the vacuum and all its sensors/selects).")] string? area = null,
        [Description("Maximum number of entities to return (default 100)")] int? limit = 100,
        CancellationToken ct = default)
    {
        return ToolResponse.Create(await RunAsync(domain, area, limit, ct));
    }
}
