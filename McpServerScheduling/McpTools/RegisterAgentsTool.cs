using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public sealed class RegisterAgentsTool(IMutableAgentCatalog catalog)
{
    [McpServerTool(Name = ChannelProtocol.RegisterAgentsTool)]
    [Description("Register the set of agents that schedules may target (replaces any previously registered set)")]
    public string McpRun([Description("Agents that schedules may target")] IReadOnlyList<AgentCatalogEntry> agents)
    {
        catalog.Replace(agents);
        return $"registered {agents.Count} agents";
    }
}