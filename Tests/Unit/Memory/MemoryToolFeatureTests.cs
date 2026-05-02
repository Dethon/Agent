using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Memory;
using Moq;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryToolFeatureTests
{
    private readonly Mock<IMemoryStore> _store = new();
    private readonly Mock<IEmbeddingService> _embedding = new();

    private MemoryToolFeature CreateFeature() => new(_store.Object, _embedding.Object);

    [Fact]
    public void FeatureName_ReturnsMemory()
    {
        CreateFeature().FeatureName.ShouldBe("memory");
    }

    [Fact]
    public void GetTools_ReturnsMemoryForgetTool()
    {
        var tools = CreateFeature().GetTools(new FeatureConfig()).ToList();
        tools.Count.ShouldBe(1);
        tools[0].Name.ShouldBe("domain__memory__memory_forget");
    }
}
