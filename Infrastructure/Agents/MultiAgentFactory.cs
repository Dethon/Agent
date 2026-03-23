using System.Collections.Concurrent;
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
    IDomainToolRegistry domainToolRegistry,
    IMetricsPublisher? metricsPublisher = null) : IAgentFactory, IScheduleAgentFactory
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, AgentDefinition>> _customAgents = new();

    public DisposableAgent Create(AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler)
    {
        var agents = registryOptions.CurrentValue.Agents;
        if (agents.Length == 0)
        {
            throw new InvalidOperationException("No agents configured.");
        }

        var agent = string.IsNullOrEmpty(agentId)
            ? agents[0]
            : agents.FirstOrDefault(a => a.Id == agentId);

        if (agent is null && agentId is not null &&
            _customAgents.TryGetValue(userId, out var userAgents) &&
            userAgents.TryGetValue(agentId, out var customAgent))
        {
            agent = customAgent;
        }

        _ = agent ?? throw new InvalidOperationException($"No agent found for identifier '{agentId}'.");

        return CreateFromDefinition(agentKey, userId, agent, approvalHandler);
    }

    public IReadOnlyList<AgentInfo> GetAvailableAgents(string? userId = null)
    {
        var builtIn = registryOptions.CurrentValue.Agents
            .Select(a => new AgentInfo(a.Id, a.Name, a.Description))
            .ToList();

        if (userId is not null &&
            _customAgents.TryGetValue(userId, out var userAgents))
        {
            builtIn.AddRange(userAgents.Values.Select(a => new AgentInfo(a.Id, a.Name, a.Description)));
        }

        return builtIn;
    }

    public AgentInfo RegisterCustomAgent(string userId, CustomAgentRegistration registration)
    {
        var id = $"custom-{Guid.NewGuid()}";
        var definition = new AgentDefinition
        {
            Id = id,
            Name = registration.Name,
            Description = registration.Description,
            Model = registration.Model,
            McpServerEndpoints = registration.McpServerEndpoints,
            WhitelistPatterns = registration.WhitelistPatterns,
            CustomInstructions = registration.CustomInstructions,
            EnabledFeatures = registration.EnabledFeatures
        };

        var userAgents = _customAgents.GetOrAdd(userId, _ => new ConcurrentDictionary<string, AgentDefinition>());
        userAgents[id] = definition;

        return new AgentInfo(id, registration.Name, registration.Description);
    }

    public bool UnregisterCustomAgent(string userId, string agentId)
    {
        if (!_customAgents.TryGetValue(userId, out var userAgents))
        {
            return false;
        }

        return userAgents.TryRemove(agentId, out _);
    }

    public DisposableAgent CreateFromDefinition(AgentKey agentKey, string userId, AgentDefinition definition, IToolApprovalHandler approvalHandler)
    {
        var chatClient = CreateChatClient(definition.Model);
        var stateStore = serviceProvider.GetRequiredService<IThreadStateStore>();

        var name = $"{definition.Name}-{agentKey.ConversationId}";
        var effectiveClient = new ToolApprovalChatClient(chatClient, approvalHandler, definition.WhitelistPatterns, metricsPublisher);

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
            model,
            metricsPublisher);
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