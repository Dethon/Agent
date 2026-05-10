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
        [Description("Optional domain filter, e.g. 'vacuum', 'light'")] string? domain = null,
        CancellationToken ct = default)
    {
        return ToolResponse.Create(await RunAsync(domain, ct));
    }
}
