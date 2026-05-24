using System.ComponentModel;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public sealed class RegisterAgentsTool(IMutableAgentCatalog catalog)
{
    private static readonly JsonSerializerOptions _options = new() { PropertyNameCaseInsensitive = true };

    [McpServerTool(Name = "register_agents")]
    [Description("Register the set of agents that schedules may target (replaces any previously registered set)")]
    public string McpRun([Description("JSON array of {id, name, description}")] string agents)
    {
        var entries = JsonSerializer.Deserialize<List<AgentCatalogEntry>>(agents, _options) ?? [];
        catalog.Replace(entries);
        return $"registered {entries.Count} agents";
    }
}