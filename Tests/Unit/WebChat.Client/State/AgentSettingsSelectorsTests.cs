using Domain.DTOs.Channel;
using Shouldly;
using WebChat.Client.State.AgentSettings;

namespace Tests.Unit.WebChat.Client.State;

public class AgentSettingsSelectorsTests
{
    private static readonly AgentCatalogEntry _jack = new(
        "jack", "Jack", null,
        "openai/gpt-5.6-luna", "low",
        [new PatchableModel("openai/gpt-5.6-luna", "GPT Luna"), new PatchableModel("z-ai/glm-5.2", "GLM 5.2")],
        AgentConfigPatch.SupportedEfforts);

    private static AgentSettingsState StateWith(AgentModelSettings settings) =>
        new() { ByAgent = new Dictionary<string, AgentModelSettings> { ["jack"] = settings } };

    [Fact]
    public void GetConfigPatch_AllValuesMatchDefaults_ReturnsNull()
    {
        var state = StateWith(new AgentModelSettings("openai/gpt-5.6-luna", "low"));

        AgentSettingsSelectors.GetConfigPatch(state, [_jack], "jack").ShouldBeNull();
    }

    [Fact]
    public void GetConfigPatch_ModelDiffers_ReturnsModelOnlyPatch()
    {
        var state = StateWith(new AgentModelSettings("z-ai/glm-5.2", "low"));

        AgentSettingsSelectors.GetConfigPatch(state, [_jack], "jack")
            .ShouldBe(new AgentConfigPatch { Model = "z-ai/glm-5.2" });
    }

    [Fact]
    public void GetConfigPatch_BothDiffer_ReturnsBothFields()
    {
        var state = StateWith(new AgentModelSettings("z-ai/glm-5.2", "max"));

        AgentSettingsSelectors.GetConfigPatch(state, [_jack], "jack")
            .ShouldBe(new AgentConfigPatch { Model = "z-ai/glm-5.2", ReasoningEffort = "max" });
    }

    [Fact]
    public void GetConfigPatch_UnknownAgent_ReturnsNull()
    {
        var state = StateWith(new AgentModelSettings("z-ai/glm-5.2", "max"));

        AgentSettingsSelectors.GetConfigPatch(state, [_jack], "ghost").ShouldBeNull();
    }

    [Fact]
    public void Sanitize_NonWhitelistedModel_FallsBackToDefault()
    {
        var sanitized = AgentSettingsSelectors.Sanitize(new AgentModelSettings("old/model", "low"), _jack);

        sanitized.ShouldBe(new AgentModelSettings("openai/gpt-5.6-luna", "low"));
    }

    [Fact]
    public void Sanitize_UnknownEffort_FallsBackToDefault()
    {
        var sanitized = AgentSettingsSelectors.Sanitize(new AgentModelSettings("z-ai/glm-5.2", "turbo"), _jack);

        sanitized.ShouldBe(new AgentModelSettings("z-ai/glm-5.2", "low"));
    }
}