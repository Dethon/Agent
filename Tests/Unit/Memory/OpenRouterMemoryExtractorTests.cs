using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Memory;

public class OpenRouterMemoryExtractorTests
{
    private readonly Mock<IChatClient> _chatClient = new();
    private readonly Mock<IMemoryStore> _store = new();
    private readonly OpenRouterMemoryExtractor _extractor;

    public OpenRouterMemoryExtractorTests()
    {
        _extractor = new OpenRouterMemoryExtractor(
            _chatClient.Object,
            _store.Object,
            Mock.Of<ILogger<OpenRouterMemoryExtractor>>());
    }

    [Fact]
    public async Task ExtractAsync_WithStorableFacts_ReturnsCandidates()
    {
        var extractionJson = """
            {
              "candidates": [
                {
                  "content": "Works at Contoso",
                  "category": "fact",
                  "importance": 0.8,
                  "confidence": 0.9,
                  "tags": ["work", "company"],
                  "context": "User mentioned during introduction"
                }
              ]
            }
            """;

        _store.Setup(s => s.GetProfileAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonalityProfile?)null);

        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, extractionJson)));

        var result = await _extractor.ExtractAsync("Hello, I work at Contoso", "user1", CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Content.ShouldBe("Works at Contoso");
        result[0].Category.ShouldBe(MemoryCategory.Fact);
        result[0].Importance.ShouldBe(0.8);
    }

    [Fact]
    public async Task ExtractAsync_WithEmptyArray_ReturnsEmpty()
    {
        _store.Setup(s => s.GetProfileAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonalityProfile?)null);

        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"candidates": []}""")));

        var result = await _extractor.ExtractAsync("Just saying hi", "user1", CancellationToken.None);
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_WithMalformedJson_ReturnsEmpty()
    {
        _store.Setup(s => s.GetProfileAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonalityProfile?)null);

        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not json at all")));

        var result = await _extractor.ExtractAsync("Hello", "user1", CancellationToken.None);
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_IncludesExistingProfileInPrompt()
    {
        var profile = new PersonalityProfile
        {
            UserId = "user1",
            Summary = "Senior .NET developer who prefers concise responses",
            LastUpdated = DateTimeOffset.UtcNow
        };

        _store.Setup(s => s.GetProfileAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);

        IEnumerable<ChatMessage>? capturedMessages = null;
        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) => capturedMessages = msgs)
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"candidates": []}""")));

        await _extractor.ExtractAsync("Hello", "user1", CancellationToken.None);

        capturedMessages.ShouldNotBeNull();
        var userMsg = capturedMessages.Last();
        userMsg.Text.ShouldContain("Senior .NET developer");
    }
}
