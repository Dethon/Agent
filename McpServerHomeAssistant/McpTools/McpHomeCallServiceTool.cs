using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class McpHomeCallServiceTool(IHomeAssistantClient client) : HomeCallServiceTool(client)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        [Description("Service domain, e.g. 'vacuum', 'light'")] string domain,
        [Description("Service name, e.g. 'start', 'turn_on'")] string service,
        [Description("Optional target entity_id, e.g. 'vacuum.roborock_s8'")] string? entityId = null,
        [Description("Optional service-specific data as a JSON object, e.g. {\"brightness_pct\": 60}")]
        JsonObject? data = null,
        CancellationToken ct = default)
    {
        return ToolResponse.Create(await RunAsync(domain, service, entityId, data, ct));
    }
}
