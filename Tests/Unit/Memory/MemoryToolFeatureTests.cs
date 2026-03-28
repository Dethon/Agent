using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Memory;
using Moq;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryToolFeatureTests
{
    [Fact]
    public void FeatureName_ReturnsMemory()
    {
        var store = new Mock<IMemoryStore>();
        var feature = new MemoryToolFeature(store.Object);
        feature.FeatureName.ShouldBe("memory");
    }

    [Fact]
    public void GetTools_ReturnsMemoryForgetTool()
    {
        var store = new Mock<IMemoryStore>();
        var feature = new MemoryToolFeature(store.Object);
        var tools = feature.GetTools(new FeatureConfig()).ToList();
        tools.Count.ShouldBe(1);
        tools[0].Name.ShouldBe("domain:memory:memory_forget");
    }

    [Fact]
    public void Prompt_ReturnsSimplifiedMemoryPrompt()
    {
        var store = new Mock<IMemoryStore>();
        var feature = new MemoryToolFeature(store.Object);
        feature.Prompt.ShouldNotBeNull();
        feature.Prompt.ShouldContain("memory_forget");
        feature.Prompt.ShouldNotContain("memory_recall");
    }
}
