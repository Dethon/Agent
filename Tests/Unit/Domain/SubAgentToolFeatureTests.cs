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

    [Fact]
    public void Prompt_DocumentsBackgroundFlagAndHelperTools()
    {
        var prompt = SubAgentPrompt.SystemPrompt;
        prompt.ShouldContain("run_in_background");
        prompt.ShouldContain("silent");
        prompt.ShouldContain("subagent_check");
        prompt.ShouldContain("subagent_wait");
        prompt.ShouldContain("subagent_cancel");
        prompt.ShouldContain("subagent_list");
    }
}
