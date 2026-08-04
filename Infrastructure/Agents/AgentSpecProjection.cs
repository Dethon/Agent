using Domain.Agents;
using Domain.DTOs;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

// The two entry points read side by side. A difference between a top-level agent and a
// subagent belongs here, as a field with a value, and never downstream as an argument one
// path stops passing.
internal static class AgentSpecProjection
{
    public static AgentSpec ForAgent(
        AgentDefinition definition,
        AgentKey agentKey,
        string userId,
        OpenRouterConfig openRouterConfig,
        ILogger? logger) => new()
        {
            DisplayName = $"{definition.Name}-{agentKey.ConversationId}",
            Description = definition.Description ?? "",
            MetricsAgentId = definition.Name,
            RoutingSessionId = $"{definition.Id}:{agentKey.ConversationId}",
            ConversationId = agentKey.ConversationId,
            UserId = userId,
            Model = definition.Model,
            MaxContextTokens = definition.MaxContextTokens ?? openRouterConfig.MaxContextTokens,
            ReasoningEffort = definition.ReasoningEffort,
            ProviderRouting = ProviderRoutingResolver.Resolve(
                definition.ProviderRouting, openRouterConfig.ProviderRouting,
                definition.Model, definition.Id, logger),
            McpServerEndpoints = definition.McpServerEndpoints,
            EnabledFeatures = definition.EnabledFeatures,
            WhitelistPatterns = definition.WhitelistPatterns,
            CustomInstructions = definition.CustomInstructions,
            Language = definition.Language,
            KeepsHistory = true,
            PatchableModelIds = openRouterConfig.PatchableModelIds ?? []
        };

    public static AgentSpec ForSubAgent(
        SubAgentDefinition definition,
        string conversationId,
        string[] whitelistPatterns,
        string userId,
        OpenRouterConfig openRouterConfig,
        ILogger? logger)
    {
        var identity = $"subagent-{definition.Id}";

        return new AgentSpec
        {
            DisplayName = identity,
            Description = definition.Description ?? "",
            MetricsAgentId = definition.Name,
            // Fresh every spawn, so a subagent never shares the parent's prompt cache: its
            // static prefix is its own instructions and its own tools, and sticking it to the
            // parent's session would route the two to the same cached prefix.
            RoutingSessionId = $"{identity}:{Guid.NewGuid():N}",
            // The parent's conversation, deliberately: a subagent acts on the parent's behalf,
            // so its metrics answer "which conversation was this slow subagent running in".
            ConversationId = conversationId,
            UserId = userId,
            Model = definition.Model,
            MaxContextTokens = definition.MaxContextTokens ?? openRouterConfig.MaxContextTokens,
            ReasoningEffort = definition.ReasoningEffort,
            ProviderRouting = ProviderRoutingResolver.Resolve(
                definition.ProviderRouting, openRouterConfig.ProviderRouting,
                definition.Model, identity, logger),
            McpServerEndpoints = definition.McpServerEndpoints,
            // A subagent cannot spawn subagents.
            EnabledFeatures = [.. definition.EnabledFeatures
                .Where(f => !f.Equals("subagents", StringComparison.OrdinalIgnoreCase))],
            WhitelistPatterns = whitelistPatterns,
            CustomInstructions = definition.CustomInstructions,
            Language = definition.Language,
            KeepsHistory = false,
            // A config patch names a model from the parent's whitelist and an effort chosen for
            // the parent's job; a subagent runs the model its own definition configures, which
            // is the point of having one. No patch reaches a subagent today, so this is the
            // second line of defence: if a future change ever copies the parent's message
            // properties down, the patch is rejected and logged instead of silently winning.
            PatchableModelIds = []
        };
    }
}