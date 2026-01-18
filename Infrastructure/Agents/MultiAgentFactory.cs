using System.Security.Cryptography;
using System.Text;
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
    OpenRouterConfig openRouterConfig) : IAgentFactory
{
    public DisposableAgent Create(AgentKey agentKey, string userId, string? botTokenHash)
    {
        var agents = registryOptions.CurrentValue.Agents;
        if (agents.Length == 0)
        {
            throw new InvalidOperationException("No agents configured.");
        }

        AgentDefinition? agent;
        if (string.IsNullOrEmpty(botTokenHash))
        {
            agent = agents[0]; // CLI mode - use first agent
        }
        else
        {
            // Try matching by Telegram bot token hash first, then by agent ID (for web chat)
            agent = agents.FirstOrDefault(a =>
                a.TelegramBotToken is not null &&
                ComputeHash(a.TelegramBotToken) == botTokenHash);
            agent ??= agents.FirstOrDefault(a => a.Id == botTokenHash);
        }

        _ = agent ?? throw new InvalidOperationException($"No agent found for identifier '{botTokenHash}'.");

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

        return new McpAgent(
            definition.McpServerEndpoints,
            effectiveClient,
            name,
            definition.Description ?? "",
            stateStore,
            userId,
            definition.CustomInstructions);
    }

    private static string ComputeHash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(bytes);
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