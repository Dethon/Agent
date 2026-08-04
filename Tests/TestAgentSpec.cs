using Infrastructure.Agents;

namespace Tests;

// One default for the construction sites that care about two or three fields. Each test
// varies exactly what it is about with `with`, so what a test depends on is what it names.
internal static class TestAgentSpec
{
    public static AgentSpec Default => new()
    {
        DisplayName = "test-agent",
        Description = "",
        MetricsAgentId = "test-agent",
        RoutingSessionId = "test-agent:conv-test",
        ConversationId = "conv-test",
        UserId = "test-user",
        Model = "test-model",
        McpServerEndpoints = [],
        EnabledFeatures = [],
        WhitelistPatterns = [],
        KeepsHistory = true,
        PatchableModelIds = []
    };
}