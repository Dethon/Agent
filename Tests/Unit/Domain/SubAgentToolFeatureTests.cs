using Domain.DTOs;
using Domain.Prompts;
using Domain.Tools.SubAgents;
using Shouldly;

namespace Tests.Unit.Domain;

public sealed class SubAgentToolFeatureTests
{
    [Fact]
    public void Prompt_ReturnsSubAgentSystemPrompt()
    {
        var registryOptions = new SubAgentRegistryOptions { SubAgents = [] };
        var feature = new SubAgentToolFeature(registryOptions);

        feature.Prompt.ShouldBe(SubAgentPrompt.SystemPrompt);
    }
}