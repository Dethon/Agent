using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Memory;

public class OpenRouterMemoryConsolidatorTests
{
    private readonly Mock<IChatClient> _chatClient = new();
    private readonly OpenRouterMemoryConsolidator _consolidator;

    public OpenRouterMemoryConsolidatorTests()
    {
        _consolidator = new OpenRouterMemoryConsolidator(
            _chatClient.Object,
            Mock.Of<ILogger<OpenRouterMemoryConsolidator>>());
    }

    [Fact]
    public async Task ConsolidateAsync_WithMergeDecision_ReturnsMergeAction()
    {
        var mergeJson = """
            [{"sourceIds": ["mem_1", "mem_2"], "action": "merge", "mergedContent": "Works at Contoso on .NET projects", "category": "fact", "importance": 0.85, "tags": ["work"]}]
            """;

        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, mergeJson)));

        var memories = new[]
        {
            CreateMemory("mem_1", "Works at Contoso"),
            CreateMemory("mem_2", "Works on .NET projects")
        };

        var result = await _consolidator.ConsolidateAsync(memories, CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Action.ShouldBe(MergeAction.Merge);
        result[0].SourceIds.ShouldBe(new[] { "mem_1", "mem_2" });
        result[0].MergedContent.ShouldBe("Works at Contoso on .NET projects");
        result[0].Category.ShouldBe(MemoryCategory.Fact);
        result[0].Importance.ShouldBe(0.85);
        result[0].Tags.ShouldBe(new[] { "work" });
    }

    [Fact]
    public async Task ConsolidateAsync_WithEmptyResponse_ReturnsEmpty()
    {
        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));

        var memories = new[] { CreateMemory("mem_1", "Some memory") };

        var result = await _consolidator.ConsolidateAsync(memories, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SynthesizeProfileAsync_ReturnsPersonalityProfile()
    {
        var profileJson = """
            {
              "summary": "A senior .NET developer at Contoso who prefers concise technical answers.",
              "communicationStyle": {
                "preference": "concise and technical",
                "avoidances": ["long explanations", "marketing speak"],
                "appreciated": ["code examples", "direct answers"]
              },
              "technicalContext": {
                "expertise": [".NET", "C#", "Azure"],
                "learning": ["Rust", "WebAssembly"],
                "stack": [".NET 10", "Redis", "Docker"]
              },
              "interactionGuidelines": ["Be direct", "Prefer code over prose"],
              "activeProjects": ["Agent AI assistant", "Idealista scraper"]
            }
            """;

        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, profileJson)));

        var memories = Enumerable.Range(1, 5)
            .Select(i => CreateMemory($"mem_{i}", $"Memory content {i}"))
            .ToArray();

        var result = await _consolidator.SynthesizeProfileAsync("user1", memories, CancellationToken.None);

        result.UserId.ShouldBe("user1");
        result.Summary.ShouldBe("A senior .NET developer at Contoso who prefers concise technical answers.");
        result.BasedOnMemoryCount.ShouldBe(5);
        result.Confidence.ShouldBe(Math.Min(1.0, 5.0 / 20));

        result.CommunicationStyle.ShouldNotBeNull();
        result.CommunicationStyle!.Preference.ShouldBe("concise and technical");
        result.CommunicationStyle.Avoidances.ShouldBe(new[] { "long explanations", "marketing speak" });
        result.CommunicationStyle.Appreciated.ShouldBe(new[] { "code examples", "direct answers" });

        result.TechnicalContext.ShouldNotBeNull();
        result.TechnicalContext!.Expertise.ShouldBe(new[] { ".NET", "C#", "Azure" });
        result.TechnicalContext.Learning.ShouldBe(new[] { "Rust", "WebAssembly" });
        result.TechnicalContext.Stack.ShouldBe(new[] { ".NET 10", "Redis", "Docker" });

        result.InteractionGuidelines.ShouldBe(new[] { "Be direct", "Prefer code over prose" });
        result.ActiveProjects.ShouldBe(new[] { "Agent AI assistant", "Idealista scraper" });
    }

    private static MemoryEntry CreateMemory(string id, string content) => new()
    {
        Id = id,
        UserId = "user1",
        Category = MemoryCategory.Fact,
        Content = content,
        Importance = 0.7,
        Confidence = 0.9,
        CreatedAt = DateTimeOffset.UtcNow,
        LastAccessedAt = DateTimeOffset.UtcNow
    };
}
