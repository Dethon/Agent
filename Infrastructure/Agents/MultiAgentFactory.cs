using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Agents;

public sealed class MultiAgentFactory(
    IServiceProvider serviceProvider,
    IAgentDefinitionProvider definitionProvider,
    OpenRouterConfig openRouterConfig,
    IDomainToolRegistry domainToolRegistry,
    IMetricsPublisher? metricsPublisher = null) : IAgentFactory, IScheduleAgentFactory
{

    public DisposableAgent Create(AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler)
    {
        var agents = definitionProvider.GetAll(userId);

        var definition = string.IsNullOrEmpty(agentId)
            ? agents.FirstOrDefault()
            : agents.FirstOrDefault(a => a.Id == agentId);

        _ = definition ?? throw new InvalidOperationException(
            string.IsNullOrEmpty(agentId)
                ? "No agents configured."
                : $"No agent found for identifier '{agentId}'.");

        return CreateFromDefinition(agentKey, userId, definition, approvalHandler);
    }

    public DisposableAgent CreateSubAgent(
        SubAgentDefinition definition,
        IToolApprovalHandler approvalHandler,
        string[] whitelistPatterns,
        string userId)
    {
        var agentPublisher = metricsPublisher is not null
            ? new AgentMetricsPublisher(metricsPublisher, definition.Name)
            : null;

        var chatClient = CreateChatClient(definition.Model, agentPublisher);

        var effectiveClient = new ToolApprovalChatClient(
            chatClient,
            approvalHandler,
            whitelistPatterns,
            agentPublisher);

        var enabledFeatures = definition.EnabledFeatures
            .Where(f => !f.Equals("subagents", StringComparison.OrdinalIgnoreCase));

        var featureConfig = new FeatureConfig(
            SubAgentFactory: def => CreateSubAgent(def, approvalHandler, whitelistPatterns, userId));
        var domainTools = domainToolRegistry
            .GetToolsForFeatures(enabledFeatures, featureConfig)
            .ToList();
        var domainPrompts = domainToolRegistry
            .GetPromptsForFeatures(enabledFeatures)
            .ToList();

        return new McpAgent(
            definition.McpServerEndpoints,
            effectiveClient,
            $"subagent-{definition.Id}",
            definition.Description ?? "",
            new NullThreadStateStore(),
            userId,
            definition.CustomInstructions,
            domainTools,
            domainPrompts,
            enableResourceSubscriptions: false);
    }

    public DisposableAgent CreateFromDefinition(AgentKey agentKey, string userId, AgentDefinition definition, IToolApprovalHandler approvalHandler)
    {
        var agentPublisher = metricsPublisher is not null
            ? new AgentMetricsPublisher(metricsPublisher, definition.Name)
            : metricsPublisher;
        var chatClient = CreateChatClient(definition.Model, agentPublisher);
        var stateStore = serviceProvider.GetRequiredService<IThreadStateStore>();

        var name = $"{definition.Name}-{agentKey.ConversationId}";
        var effectiveClient = new ToolApprovalChatClient(chatClient, approvalHandler, definition.WhitelistPatterns, agentPublisher);

        var featureConfig = new FeatureConfig(
            SubAgentFactory: def => CreateSubAgent(def, approvalHandler, definition.WhitelistPatterns, userId));
        var domainTools = domainToolRegistry
            .GetToolsForFeatures(definition.EnabledFeatures, featureConfig)
            .ToList();
        var domainPrompts = domainToolRegistry
            .GetPromptsForFeatures(definition.EnabledFeatures)
            .ToList();

        return new McpAgent(
            definition.McpServerEndpoints,
            effectiveClient,
            name,
            definition.Description ?? "",
            stateStore,
            userId,
            definition.CustomInstructions,
            domainTools,
            domainPrompts);
    }

    private OpenRouterChatClient CreateChatClient(string model, IMetricsPublisher? publisher = null)
    {
        return new OpenRouterChatClient(
            openRouterConfig.ApiUrl,
            openRouterConfig.ApiKey,
            model,
            publisher ?? metricsPublisher);
    }
}

public record OpenRouterConfig
{
    public required string ApiUrl { get; init; }
    public required string ApiKey { get; init; }
}

public sealed class AgentRegistryOptions
{
    public AgentDefinition[] Agents { get; set; } = [];
}