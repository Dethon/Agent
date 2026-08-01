using Domain.DTOs.Channel;
using Infrastructure.Agents.ChatClients;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

public class OpenRouterModelOverrideTests
{
    private static readonly IReadOnlyList<string> Whitelist = ["openai/gpt-5.6-luna", "z-ai/glm-5.2"];

    [Fact]
    public void ResolveModelOverride_WhitelistedDifferentModel_ReturnsIt()
    {
        var patch = new AgentConfigPatch { Model = "z-ai/glm-5.2" };

        OpenRouterChatClient.ResolveModelOverride(patch, "openai/gpt-5.6-luna", Whitelist)
            .ShouldBe("z-ai/glm-5.2");
    }

    [Fact]
    public void ResolveModelOverride_NonWhitelistedModel_ReturnsNull()
    {
        var patch = new AgentConfigPatch { Model = "evil/model" };

        OpenRouterChatClient.ResolveModelOverride(patch, "openai/gpt-5.6-luna", Whitelist).ShouldBeNull();
    }

    [Fact]
    public void ResolveModelOverride_SameAsConfigured_ReturnsNull()
    {
        var patch = new AgentConfigPatch { Model = "openai/gpt-5.6-luna" };

        OpenRouterChatClient.ResolveModelOverride(patch, "openai/gpt-5.6-luna", Whitelist).ShouldBeNull();
    }

    [Fact]
    public void ResolveModelOverride_NullPatch_ReturnsNull()
    {
        OpenRouterChatClient.ResolveModelOverride(null, "openai/gpt-5.6-luna", Whitelist).ShouldBeNull();
    }

    [Fact]
    public void ResolveModelOverride_DifferentCasing_ReturnsWhitelistCanonicalCasing()
    {
        var patch = new AgentConfigPatch { Model = "Z-AI/GLM-5.2" };

        OpenRouterChatClient.ResolveModelOverride(patch, "openai/gpt-5.6-luna", Whitelist)
            .ShouldBe("z-ai/glm-5.2");
    }
}