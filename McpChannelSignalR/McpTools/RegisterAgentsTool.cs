using System.ComponentModel;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class RegisterAgentsTool(IMutableAgentCatalog catalog, IHubNotificationSender hubSender)
{
    private static readonly JsonSerializerOptions _options = new() { PropertyNameCaseInsensitive = true };

    [McpServerTool(Name = "register_agents")]
    [Description("Register the agents available to WebChat (replaces any previously registered set)")]
    public string McpRun([Description("JSON array of {id, name, description}")] string agents)
    {
        var entries = JsonSerializer.Deserialize<List<AgentCatalogEntry>>(agents, _options) ?? [];
        catalog.Replace(entries);
        _ = hubSender.SendAsync("OnAgentsUpdated", entries);
        return $"registered {entries.Count} agents";
    }
}