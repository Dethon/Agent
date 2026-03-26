using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents;

public sealed class SubAgentRunner(
    OpenRouterConfig openRouterConfig,
    Lazy<IDomainToolRegistry> domainToolRegistry,
    IMetricsPublisher? metricsPublisher = null) : ISubAgentRunner
{
    public async Task<string> RunAsync(
        SubAgentDefinition definition,
        string prompt,
        FeatureConfig parentContext,
        CancellationToken ct = default)
    {
        var agentPublisher = metricsPublisher is not null
            ? new AgentMetricsPublisher(metricsPublisher, definition.Id)
            : null;

        using var chatClient = new OpenRouterChatClient(
            openRouterConfig.ApiUrl,
            openRouterConfig.ApiKey,
            definition.Model,
            agentPublisher);

        using var effectiveClient = new ToolApprovalChatClient(
            chatClient,
            parentContext.ApprovalHandler,
            parentContext.WhitelistPatterns,
            agentPublisher);

        var enabledFeatures = definition.EnabledFeatures
            .Where(f => !f.Equals("subagents", StringComparison.OrdinalIgnoreCase));

        var domainTools = domainToolRegistry.Value
            .GetToolsForFeatures(enabledFeatures, parentContext)
            .ToList();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(definition.MaxExecutionSeconds));

        await using var agent = new McpAgent(
            definition.McpServerEndpoints,
            effectiveClient,
            $"subagent-{definition.Id}",
            definition.Description ?? "",
            new NullThreadStateStore(),
            parentContext.UserId,
            definition.CustomInstructions,
            domainTools,
            enableResourceSubscriptions: false);

        var userMessage = new ChatMessage(ChatRole.User, prompt);
        var response = await agent.RunStreamingAsync(
                [userMessage], cancellationToken: timeoutCts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(timeoutCts.Token);

        return string.Join("", response.Select(r => r.Content).Where(c => !string.IsNullOrEmpty(c)));
    }
}
