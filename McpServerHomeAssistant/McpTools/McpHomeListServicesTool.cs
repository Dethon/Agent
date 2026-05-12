using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.HomeAssistant;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class McpHomeListServicesTool(IHomeAssistantClient client) : HomeListServicesTool(client)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        [Description("Optional domain filter. Many integrations register their own services under a separate domain matching the integration name (e.g. a Tuya light's vendor actions live under 'tuya.*', not 'light.*'), so omit this when discovering capabilities of an integration you haven't mapped yet.")] string? domain = null,
        CancellationToken ct = default)
    {
        return ToolResponse.Create(await RunAsync(domain, ct));
    }
}
