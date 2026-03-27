using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public sealed class DomainToolRegistryTests
{
    [Fact]
    public void GetPromptsForFeatures_EnabledFeatureWithPrompt_ReturnsPrompt()
    {
        var feature = new Mock<IDomainToolFeature>();
        feature.Setup(f => f.FeatureName).Returns("subagents");
        feature.Setup(f => f.Prompt).Returns("Use subagents proactively.");
        feature.Setup(f => f.GetTools(It.IsAny<FeatureConfig>())).Returns([]);

        var registry = new DomainToolRegistry([feature.Object]);

        var prompts = registry.GetPromptsForFeatures(["subagents"]).ToList();

        prompts.ShouldBe(["Use subagents proactively."]);
    }

    [Fact]
    public void GetPromptsForFeatures_FeatureWithNullPrompt_ReturnsEmpty()
    {
        var feature = new Mock<IDomainToolFeature>();
        feature.Setup(f => f.FeatureName).Returns("scheduling");
        feature.Setup(f => f.Prompt).Returns((string?)null);
        feature.Setup(f => f.GetTools(It.IsAny<FeatureConfig>())).Returns([]);

        var registry = new DomainToolRegistry([feature.Object]);

        var prompts = registry.GetPromptsForFeatures(["scheduling"]).ToList();

        prompts.ShouldBeEmpty();
    }

    [Fact]
    public void GetPromptsForFeatures_DisabledFeature_ReturnsEmpty()
    {
        var feature = new Mock<IDomainToolFeature>();
        feature.Setup(f => f.FeatureName).Returns("subagents");
        feature.Setup(f => f.Prompt).Returns("Use subagents proactively.");
        feature.Setup(f => f.GetTools(It.IsAny<FeatureConfig>())).Returns([]);

        var registry = new DomainToolRegistry([feature.Object]);

        var prompts = registry.GetPromptsForFeatures(["scheduling"]).ToList();

        prompts.ShouldBeEmpty();
    }

    [Fact]
    public void GetPromptsForFeatures_MultipleFeatures_ReturnsOnlyNonNullPrompts()
    {
        var withPrompt = new Mock<IDomainToolFeature>();
        withPrompt.Setup(f => f.FeatureName).Returns("subagents");
        withPrompt.Setup(f => f.Prompt).Returns("Delegate work.");
        withPrompt.Setup(f => f.GetTools(It.IsAny<FeatureConfig>())).Returns([]);

        var withoutPrompt = new Mock<IDomainToolFeature>();
        withoutPrompt.Setup(f => f.FeatureName).Returns("scheduling");
        withoutPrompt.Setup(f => f.Prompt).Returns((string?)null);
        withoutPrompt.Setup(f => f.GetTools(It.IsAny<FeatureConfig>())).Returns([]);

        var registry = new DomainToolRegistry([withPrompt.Object, withoutPrompt.Object]);

        var prompts = registry.GetPromptsForFeatures(["subagents", "scheduling"]).ToList();

        prompts.ShouldBe(["Delegate work."]);
    }
}
