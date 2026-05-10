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
        [Description("Optional domain filter, e.g. 'vacuum', 'light', 'climate'")] string? domain = null,
        [Description("Optional substring to match against friendly_name")] string? area = null,
        [Description("Maximum number of entities to return (default 100)")] int? limit = 100,
        CancellationToken ct = default)
    {
        return ToolResponse.Create(await RunAsync(domain, area, limit, ct));
    }
}
