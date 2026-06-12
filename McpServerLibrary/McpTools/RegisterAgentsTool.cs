using System.ComponentModel;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public sealed class RegisterAgentsTool
{
    [McpServerTool(Name = ChannelProtocol.RegisterAgentsTool)]
    [Description("Register the agent catalog — the library channel does not target agents; the set is ignored")]
    public static string McpRun([Description("Registered agents")] IReadOnlyList<AgentCatalogEntry> agents)
        => $"registered {agents.Count} agents";
}