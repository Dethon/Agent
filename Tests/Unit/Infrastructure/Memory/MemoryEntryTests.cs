using Domain.Contracts;
using Domain.DTOs;
using Shouldly;

namespace Tests.Unit.Infrastructure.Memory;

public class MemoryEntryTests
{
    [Fact]
    public void MemoryEntry_WithRequiredProperties_CanBeCreated()
    {
        // Arrange & Act
        var memory = new MemoryEntry
        {
            Id = "mem_123",
            UserId = "user_456",
            Tier = MemoryTier.LongTerm,
            Category = MemoryCategory.Preference,
            Content = "User prefers concise responses",
            Importance = 0.8,
            Confidence = 0.9,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        };

        // Assert
        memory.Id.ShouldBe("mem_123");
        memory.UserId.ShouldBe("user_456");
        memory.Tier.ShouldBe(MemoryTier.LongTerm);
        memory.Category.ShouldBe(MemoryCategory.Preference);
        memory.Content.ShouldBe("User prefers concise responses");
        memory.Importance.ShouldBe(0.8);
        memory.Confidence.ShouldBe(0.9);
    }

    [Fact]
    public void MemoryEntry_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var memory = new MemoryEntry
        {
            Id = "mem_123",
            UserId = "user_456",
            Tier = MemoryTier.LongTerm,
            Category = MemoryCategory.Fact,
            Content = "test",
            Importance = 0.5,
            Confidence = 0.7,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        };

        // Assert
        memory.Tags.ShouldBeEmpty();
        memory.AccessCount.ShouldBe(0);
        memory.DecayFactor.ShouldBe(1.0);
        memory.SupersededById.ShouldBeNull();
        memory.Context.ShouldBeNull();
        memory.Embedding.ShouldBeNull();
        memory.Source.ShouldBeNull();
    }

    [Fact]
    public void MemoryEntry_WithOptionalProperties_CanBeCreated()
    {
        // Arrange & Act
        var memory = new MemoryEntry
        {
            Id = "mem_123",
            UserId = "user_456",
            Tier = MemoryTier.MidTerm,
            Category = MemoryCategory.Project,
            Content = "Working on API project",
            Context = "Mentioned in conversation about work",
            Importance = 0.6,
            Confidence = 0.8,
            Embedding = [0.1f, 0.2f, 0.3f],
            Tags = ["work", "api", "project"],
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow,
            AccessCount = 5,
            DecayFactor = 0.9,
            Source = new MemorySource("conv_123", "msg_456")
        };

        // Assert
        memory.Context.ShouldBe("Mentioned in conversation about work");
        memory.Embedding.ShouldBe([0.1f, 0.2f, 0.3f]);
        memory.Tags.ShouldBe(["work", "api", "project"]);
        memory.AccessCount.ShouldBe(5);
        memory.DecayFactor.ShouldBe(0.9);
        memory.Source!.ConversationId.ShouldBe("conv_123");
        memory.Source!.MessageId.ShouldBe("msg_456");
    }

    [Fact]
    public void MemoryEntry_WithRecord_CanBeClonedWithChanges()
    {
        // Arrange
        var original = new MemoryEntry
        {
            Id = "mem_123",
            UserId = "user_456",
            Tier = MemoryTier.LongTerm,
            Category = MemoryCategory.Preference,
            Content = "Original content",
            Importance = 0.5,
            Confidence = 0.7,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        };

        // Act
        var updated = original with
        {
            Importance = 0.9,
            AccessCount = original.AccessCount + 1,
            LastAccessedAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };

        // Assert
        updated.Id.ShouldBe(original.Id);
        updated.Content.ShouldBe(original.Content);
        updated.Importance.ShouldBe(0.9);
        updated.AccessCount.ShouldBe(1);
        original.Importance.ShouldBe(0.5); // Original unchanged
        original.AccessCount.ShouldBe(0);
    }
}

public class PersonalityProfileTests
{
    [Fact]
    public void PersonalityProfile_WithRequiredProperties_CanBeCreated()
    {
        // Arrange & Act
        var profile = new PersonalityProfile
        {
            UserId = "user_123",
            Summary = "Technical user who prefers concise responses",
            LastUpdated = DateTimeOffset.UtcNow
        };

        // Assert
        profile.UserId.ShouldBe("user_123");
        profile.Summary.ShouldBe("Technical user who prefers concise responses");
    }

    [Fact]
    public void PersonalityProfile_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var profile = new PersonalityProfile
        {
            UserId = "user_123",
            Summary = "test",
            LastUpdated = DateTimeOffset.UtcNow
        };

        // Assert
        profile.InteractionGuidelines.ShouldBeEmpty();
        profile.ActiveProjects.ShouldBeEmpty();
        profile.Confidence.ShouldBe(0);
        profile.BasedOnMemoryCount.ShouldBe(0);
        profile.CommunicationStyle.ShouldBeNull();
        profile.TechnicalContext.ShouldBeNull();
    }

    [Fact]
    public void PersonalityProfile_WithFullData_CanBeCreated()
    {
        // Arrange & Act
        var profile = new PersonalityProfile
        {
            UserId = "user_123",
            Summary = "Senior developer who values efficiency",
            CommunicationStyle = new CommunicationStyle
            {
                Preference = "Direct and technical",
                Avoidances = ["Over-explanation", "Excessive caveats"],
                Appreciated = ["Code examples", "Performance tips"]
            },
            TechnicalContext = new TechnicalContext
            {
                Expertise = ["Go", "Kubernetes"],
                Learning = ["Rust"],
                Stack = ["Linux", "Docker"]
            },
            InteractionGuidelines = ["Skip basic explanations", "Include error handling"],
            ActiveProjects = ["Microservices migration"],
            Confidence = 0.85,
            BasedOnMemoryCount = 34,
            LastUpdated = DateTimeOffset.UtcNow
        };

        // Assert
        profile.CommunicationStyle!.Preference.ShouldBe("Direct and technical");
        profile.CommunicationStyle.Avoidances.ShouldContain("Over-explanation");
        profile.CommunicationStyle.Appreciated.ShouldContain("Code examples");
        profile.TechnicalContext!.Expertise.ShouldContain("Go");
        profile.TechnicalContext.Learning.ShouldContain("Rust");
        profile.InteractionGuidelines.Count.ShouldBe(2);
        profile.ActiveProjects.ShouldContain("Microservices migration");
        profile.Confidence.ShouldBe(0.85);
        profile.BasedOnMemoryCount.ShouldBe(34);
    }
}

public class MemorySearchResultTests
{
    [Fact]
    public void MemorySearchResult_CanBeCreated()
    {
        // Arrange
        var memory = CreateTestMemory("mem_1", "Test content");

        // Act
        var result = new MemorySearchResult(memory, 0.95);

        // Assert
        result.Memory.ShouldBe(memory);
        result.Relevance.ShouldBe(0.95);
    }

    private static MemoryEntry CreateTestMemory(string id, string content)
    {
        return new MemoryEntry
        {
            Id = id,
            UserId = "user_123",
            Tier = MemoryTier.LongTerm,
            Category = MemoryCategory.Fact,
            Content = content,
            Importance = 0.5,
            Confidence = 0.7,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        };
    }
}

public class MemoryStatsTests
{
    [Fact]
    public void MemoryStats_CanBeCreated()
    {
        // Arrange
        var byCategory = new Dictionary<MemoryCategory, int>
        {
            [MemoryCategory.Preference] = 5,
            [MemoryCategory.Fact] = 3
        };
        var byTier = new Dictionary<MemoryTier, int>
        {
            [MemoryTier.LongTerm] = 7,
            [MemoryTier.MidTerm] = 1
        };

        // Act
        var stats = new MemoryStats(8, byCategory, byTier);

        // Assert
        stats.TotalMemories.ShouldBe(8);
        stats.ByCategory[MemoryCategory.Preference].ShouldBe(5);
        stats.ByCategory[MemoryCategory.Fact].ShouldBe(3);
        stats.ByTier[MemoryTier.LongTerm].ShouldBe(7);
        stats.ByTier[MemoryTier.MidTerm].ShouldBe(1);
    }
}