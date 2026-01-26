using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.Agents;

public sealed class MultiAgentFactory(
    IServiceProvider serviceProvider,
    IOptionsMonitor<AgentRegistryOptions> registryOptions,
    OpenRouterConfig openRouterConfig,
    IDomainToolRegistry domainToolRegistry) : IAgentFactory, IScheduleAgentFactory
{
    public DisposableAgent Create(AgentKey agentKey, string userId, string? agentId)
    {
        var agents = registryOptions.CurrentValue.Agents;
        if (agents.Length == 0)
        {
            throw new InvalidOperationException("No agents configured.");
        }

        var agent = string.IsNullOrEmpty(agentId)
            ? agents[0]
            : // CLI mode - use first agent
            agents.FirstOrDefault(a => a.Id == agentId);

        _ = agent ?? throw new InvalidOperationException($"No agent found for identifier '{agentId}'.");

        return CreateFromDefinition(agentKey, userId, agent);
    }

    public IReadOnlyList<AgentInfo> GetAvailableAgents()
    {
        return registryOptions.CurrentValue.Agents
            .Select(a => new AgentInfo(a.Id, a.Name, a.Description))
            .ToList();
    }

    public DisposableAgent CreateFromDefinition(AgentKey agentKey, string userId, AgentDefinition definition)
    {
        var chatClient = CreateChatClient(definition.Model);
        var approvalHandlerFactory = serviceProvider.GetRequiredService<IToolApprovalHandlerFactory>();
        var stateStore = serviceProvider.GetRequiredService<IThreadStateStore>();

        var name = $"{definition.Name}-{agentKey.ChatId}-{agentKey.ThreadId}";
        var handler = approvalHandlerFactory.Create(agentKey);
        var effectiveClient = new ToolApprovalChatClient(chatClient, handler, definition.WhitelistPatterns);

        var domainTools = domainToolRegistry
            .GetToolsForFeatures(definition.EnabledFeatures)
            .ToList();

        return new McpAgent(
            definition.McpServerEndpoints,
            effectiveClient,
            name,
            definition.Description ?? "",
            stateStore,
            userId,
            definition.CustomInstructions,
            domainTools);
    }

    private OpenRouterChatClient CreateChatClient(string model)
    {
        return new OpenRouterChatClient(
            openRouterConfig.ApiUrl,
            openRouterConfig.ApiKey,
            model);
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