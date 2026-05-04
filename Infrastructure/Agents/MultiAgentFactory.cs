using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

public sealed class MultiAgentFactory(
    IServiceProvider serviceProvider,
    IAgentDefinitionProvider definitionProvider,
    OpenRouterConfig openRouterConfig,
    IDomainToolRegistry domainToolRegistry,
    IMetricsPublisher? metricsPublisher = null,
    ILoggerFactory? loggerFactory = null) : IAgentFactory, IScheduleAgentFactory
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

        var chatClient = CreateChatClient(definition.Model, agentPublisher, definition.MaxContextTokens);

        var effectiveClient = new ToolApprovalChatClient(
            chatClient,
            approvalHandler,
            whitelistPatterns,
            agentPublisher);

        var enabledFeatures = definition.EnabledFeatures
            .Where(f => !f.Equals("subagents", StringComparison.OrdinalIgnoreCase));

        var featureConfig = new FeatureConfig(
            SubAgentFactory: def => CreateSubAgent(def, approvalHandler, whitelistPatterns, userId),
            UserId: userId);
        var domainTools = domainToolRegistry
            .GetToolsForFeatures(enabledFeatures, featureConfig)
            .ToList();
        var domainPrompts = domainToolRegistry
            .GetPromptsForFeatures(enabledFeatures)
            .ToList();

        var filesystemEnabledTools = ExtractFilesystemEnabledTools(enabledFeatures);

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
            enableResourceSubscriptions: false,
            filesystemEnabledTools: filesystemEnabledTools,
            loggerFactory: loggerFactory);
    }

    public DisposableAgent CreateFromDefinition(AgentKey agentKey, string userId, AgentDefinition definition, IToolApprovalHandler approvalHandler)
    {
        var agentPublisher = metricsPublisher is not null
            ? new AgentMetricsPublisher(metricsPublisher, definition.Name)
            : metricsPublisher;
        var chatClient = CreateChatClient(definition.Model, agentPublisher, definition.MaxContextTokens);
        var stateStore = serviceProvider.GetRequiredService<IThreadStateStore>();

        var name = $"{definition.Name}-{agentKey.ConversationId}";
        var effectiveClient = new ToolApprovalChatClient(chatClient, approvalHandler, definition.WhitelistPatterns, agentPublisher);

        var featureConfig = new FeatureConfig(
            SubAgentFactory: def => CreateSubAgent(def, approvalHandler, definition.WhitelistPatterns, userId),
            UserId: userId);
        var domainTools = domainToolRegistry
            .GetToolsForFeatures(definition.EnabledFeatures, featureConfig)
            .ToList();
        var domainPrompts = domainToolRegistry
            .GetPromptsForFeatures(definition.EnabledFeatures)
            .ToList();

        var filesystemEnabledTools = ExtractFilesystemEnabledTools(definition.EnabledFeatures);

        return new McpAgent(
            definition.McpServerEndpoints,
            effectiveClient,
            name,
            definition.Description ?? "",
            stateStore,
            userId,
            definition.CustomInstructions,
            domainTools,
            domainPrompts,
            filesystemEnabledTools: filesystemEnabledTools,
            loggerFactory: loggerFactory);
    }

    private static IReadOnlySet<string> ExtractFilesystemEnabledTools(IEnumerable<string> enabledFeatures)
    {
        var fsParts = enabledFeatures
            .Select(f => f.Split('.', 2))
            .Where(p => p[0].Equals("filesystem", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (fsParts.Count == 0)
        {
            return new HashSet<string>();
        }

        if (fsParts.Any(p => p.Length == 1))
        {
            return FileSystemToolFeature.AllToolKeys;
        }

        return fsParts
            .Where(p => p.Length == 2)
            .Select(p => p[1])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private OpenRouterChatClient CreateChatClient(string model, IMetricsPublisher? publisher = null, int? maxContextTokens = null)
    {
        return new OpenRouterChatClient(
            openRouterConfig.ApiUrl,
            openRouterConfig.ApiKey,
            model,
            maxContextTokens ?? openRouterConfig.MaxContextTokens,
            publisher ?? metricsPublisher);
    }
}

public record OpenRouterConfig
{
    public required string ApiUrl { get; init; }
    public required string ApiKey { get; init; }
    public int? MaxContextTokens { get; init; }
}

public sealed class AgentRegistryOptions
{
    public AgentDefinition[] Agents { get; set; } = [];
}
