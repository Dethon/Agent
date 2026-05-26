using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class RegisterAgentsTool(IMutableAgentCatalog catalog, IHubNotificationSender hubSender)
{
    [McpServerTool(Name = ChannelProtocol.RegisterAgentsTool)]
    [Description("Register the agents available to WebChat (replaces any previously registered set)")]
    public string McpRun([Description("Agents available to WebChat")] IReadOnlyList<AgentCatalogEntry> agents)
    {
        catalog.Replace(agents);
        // best-effort UI refresh; a client-push failure must not block registration
        _ = hubSender.SendAsync("OnAgentsUpdated", agents);
        return $"registered {agents.Count} agents";
    }
}