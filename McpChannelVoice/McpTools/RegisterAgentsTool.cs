using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class RegisterAgentsTool(IMutableAgentCatalog catalog)
{
    [McpServerTool(Name = ChannelProtocol.RegisterAgentsTool)]
    [Description("Register the agents that voice satellites may target (replaces any previously registered set)")]
    public string McpRun([Description("Agents available to voice")] IReadOnlyList<AgentCatalogEntry> agents)
    {
        catalog.Replace(agents);
        return $"registered {agents.Count} agents";
    }
}