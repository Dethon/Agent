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
            {"decisions": [{"sourceIds": ["mem_1", "mem_2"], "action": "merge", "mergedContent": "Works at Contoso on .NET projects", "category": "fact", "importance": 0.85, "tags": ["work"]}]}
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
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"decisions": []}""")));

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

    [Fact]
    public async Task ConsolidateAsync_WithNoEmbeddings_SendsSingleCall()
    {
        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"decisions": []}""")));

        var memories = new[]
        {
            CreateMemory("mem_1", "Works at Contoso"),
            CreateMemory("mem_2", "Works on .NET projects"),
            CreateMemory("mem_3", "Unrelated thing")
        };

        await _consolidator.ConsolidateAsync(memories, CancellationToken.None);

        _chatClient.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ConsolidateAsync_WithEmbeddings_ClustersSimilarMemoriesIntoSeparateLlmCalls()
    {
        // Two clusters: cluster A (nearly identical vectors), cluster B (orthogonal)
        var clusterA1 = CreateMemory("a1", "Has a Japan Rail Pass", embedding: [1.0f, 0.0f, 0.0f]);
        var clusterA2 = CreateMemory("a2", "User has a JR Pass for the trip", embedding: [0.99f, 0.01f, 0.0f]);
        var clusterA3 = CreateMemory("a3", "Rail pass available", embedding: [0.98f, 0.02f, 0.0f]);
        var clusterB1 = CreateMemory("b1", "Watches Dragon Raja", embedding: [0.0f, 1.0f, 0.0f]);
        var clusterB2 = CreateMemory("b2", "Anime: Dragon Raja", embedding: [0.01f, 0.99f, 0.0f]);

        var capturedPrompts = new List<string>();
        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((msgs, _, _) =>
                capturedPrompts.Add(string.Join("\n", msgs.Select(m => m.Text))))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"decisions": []}""")));

        await _consolidator.ConsolidateAsync(
            [clusterA1, clusterA2, clusterA3, clusterB1, clusterB2],
            CancellationToken.None);

        // Two clusters → two LLM calls
        capturedPrompts.Count.ShouldBe(2);

        // Each cluster prompt contains only its own ids
        var promptA = capturedPrompts.Single(p => p.Contains("a1"));
        promptA.ShouldContain("a2");
        promptA.ShouldContain("a3");
        promptA.ShouldNotContain("b1");
        promptA.ShouldNotContain("b2");

        var promptB = capturedPrompts.Single(p => p.Contains("b1"));
        promptB.ShouldContain("b2");
        promptB.ShouldNotContain("a1");
    }

    [Fact]
    public async Task ConsolidateAsync_SkipsSingletonClusters()
    {
        // Singleton (no similar neighbor) has nothing to merge against → no LLM call needed.
        var loner = CreateMemory("x", "Unique thing", embedding: [1.0f, 0.0f, 0.0f]);
        var pair1 = CreateMemory("p1", "Thing A", embedding: [0.0f, 1.0f, 0.0f]);
        var pair2 = CreateMemory("p2", "Thing A restated", embedding: [0.01f, 0.99f, 0.0f]);

        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"decisions": []}""")));

        await _consolidator.ConsolidateAsync([loner, pair1, pair2], CancellationToken.None);

        _chatClient.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ConsolidateAsync_AggregatesDecisionsFromAllClusters()
    {
        var a1 = CreateMemory("a1", "A first", embedding: [1.0f, 0.0f]);
        var a2 = CreateMemory("a2", "A second", embedding: [0.99f, 0.01f]);
        var b1 = CreateMemory("b1", "B first", embedding: [0.0f, 1.0f]);
        var b2 = CreateMemory("b2", "B second", embedding: [0.01f, 0.99f]);

        _chatClient.SetupSequence(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"decisions":[{"sourceIds":["a1","a2"],"action":"merge","mergedContent":"A merged","category":"fact","importance":0.8,"tags":[]}]}""")))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"decisions":[{"sourceIds":["b1","b2"],"action":"merge","mergedContent":"B merged","category":"fact","importance":0.8,"tags":[]}]}""")));

        var result = await _consolidator.ConsolidateAsync([a1, a2, b1, b2], CancellationToken.None);

        result.Count.ShouldBe(2);
        result.Select(r => r.MergedContent).ShouldBe(new[] { "A merged", "B merged" }, ignoreOrder: true);
    }

    private static MemoryEntry CreateMemory(string id, string content, float[]? embedding = null) => new()
    {
        Id = id,
        UserId = "user1",
        Category = MemoryCategory.Fact,
        Content = content,
        Importance = 0.7,
        Confidence = 0.9,
        Embedding = embedding,
        CreatedAt = DateTimeOffset.UtcNow,
        LastAccessedAt = DateTimeOffset.UtcNow
    };
}
