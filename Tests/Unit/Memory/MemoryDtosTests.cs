using Domain.Contracts;
using Domain.DTOs;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryDtosTests
{
    [Fact]
    public void MemoryContext_ConstructsWithMemoriesAndProfile()
    {
        var memories = new List<MemorySearchResult>
        {
            new(new MemoryEntry
            {
                Id = "mem_1", UserId = "user1", Category = MemoryCategory.Preference,
                Content = "Likes concise responses", Importance = 0.9, Confidence = 0.8,
                CreatedAt = DateTimeOffset.UtcNow, LastAccessedAt = DateTimeOffset.UtcNow
            }, 0.95)
        };
        var profile = new PersonalityProfile
        {
            UserId = "user1", Summary = "Prefers brevity", LastUpdated = DateTimeOffset.UtcNow
        };
        var context = new MemoryContext(memories, profile);
        context.Memories.Count.ShouldBe(1);
        context.Memories[0].Memory.Content.ShouldBe("Likes concise responses");
        context.Profile.ShouldNotBeNull();
        context.Profile.Summary.ShouldBe("Prefers brevity");
    }

    [Fact]
    public void MemoryContext_ConstructsWithEmptyMemoriesAndNoProfile()
    {
        var context = new MemoryContext([], null);
        context.Memories.ShouldBeEmpty();
        context.Profile.ShouldBeNull();
    }

    [Fact]
    public void MemoryExtractionRequest_ConstructsWithRequiredFields()
    {
        var request = new MemoryExtractionRequest("user1", "Hello, I work at Contoso", "conv_123", "agent_1");
        request.UserId.ShouldBe("user1");
        request.MessageContent.ShouldBe("Hello, I work at Contoso");
        request.ConversationId.ShouldBe("conv_123");
        request.AgentId.ShouldBe("agent_1");
    }

    [Fact]
    public void ExtractionCandidate_ConstructsWithAllFields()
    {
        var candidate = new ExtractionCandidate(
            Content: "Works at Contoso",
            Category: MemoryCategory.Fact,
            Importance: 0.8,
            Confidence: 0.9,
            Tags: ["work", "company"],
            Context: "User mentioned during introduction");
        candidate.Content.ShouldBe("Works at Contoso");
        candidate.Category.ShouldBe(MemoryCategory.Fact);
        candidate.Importance.ShouldBe(0.8);
        candidate.Tags.Count.ShouldBe(2);
    }
}
