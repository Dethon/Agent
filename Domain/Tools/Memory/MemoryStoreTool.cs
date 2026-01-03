using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Memory;

public class MemoryStoreTool(
    IMemoryStore store,
    IEmbeddingService embeddingService)
{
    private const int SimilarMemorySearchLimit = 3;
    private const double SimilarityThreshold = 0.85;
    private const double DefaultDecayFactor = 1.0;
    private const int DefaultAccessCount = 0;

    protected const string Name = "memory_store";

    protected const string Description = """
                                         Stores a memory about the user for future recall. Use this when you learn something
                                         worth remembering—their preferences, facts about them, ongoing projects, or how they like to interact.

                                         Categories:
                                         - preference: How user likes things (communication style, format preferences)
                                         - fact: Factual info (job, location, tech stack)
                                         - relationship: Interaction patterns (inside jokes, rapport)
                                         - skill: User's expertise and learning areas
                                         - project: Current work and context
                                         - personality: How YOU should behave with this user
                                         - instruction: Explicit directives from user

                                         Set higher importance (0.7-1.0) for explicit user statements, lower (0.3-0.5) for inferred preferences.
                                         Use supersedes to update outdated memories rather than creating duplicates.
                                         """;

    protected async Task<JsonNode> Run(
        string userId,
        string content,
        string category,
        string? tier = null,
        double importance = 0.5,
        double confidence = 0.7,
        string? tags = null,
        string? context = null,
        string? supersedes = null,
        CancellationToken ct = default)
    {
        if (!TryParseCategory(category, out var memoryCategory))
        {
            return CreateCategoryErrorResponse(category);
        }

        var memoryTier = ParseTier(tier, memoryCategory);
        var embedding = await embeddingService.GenerateEmbeddingAsync(content, ct);

        var memory = CreateMemoryEntry(userId, content, memoryCategory, memoryTier, importance, confidence, tags,
            context, embedding);

        var similarMemories = await FindSimilarMemories(userId, embedding, memoryCategory, supersedes, ct);

        if (!string.IsNullOrWhiteSpace(supersedes))
        {
            await store.SupersedeAsync(userId, supersedes, memory.Id, ct);
        }

        await store.StoreAsync(memory, ct);

        return CreateSuccessResponse(memory, similarMemories);
    }

    private static bool TryParseCategory(string category, out MemoryCategory result)
    {
        return Enum.TryParse(category, ignoreCase: true, out result);
    }

    private static MemoryTier ParseTier(string? tier, MemoryCategory category)
    {
        return tier switch
        {
            "long-term" => MemoryTier.LongTerm,
            "mid-term" => MemoryTier.MidTerm,
            null => InferTier(category),
            _ => throw new ArgumentException($"Invalid tier: {tier}")
        };
    }

    private static MemoryTier InferTier(MemoryCategory category)
    {
        return category switch
        {
            MemoryCategory.Preference => MemoryTier.LongTerm,
            MemoryCategory.Fact => MemoryTier.LongTerm,
            MemoryCategory.Instruction => MemoryTier.LongTerm,
            MemoryCategory.Personality => MemoryTier.LongTerm,
            MemoryCategory.Skill => MemoryTier.LongTerm,
            MemoryCategory.Relationship => MemoryTier.LongTerm,
            _ => MemoryTier.MidTerm
        };
    }

    private static MemoryEntry CreateMemoryEntry(
        string userId,
        string content,
        MemoryCategory category,
        MemoryTier tier,
        double importance,
        double confidence,
        string? tags,
        string? context,
        float[] embedding)
    {
        return new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Tier = tier,
            Category = category,
            Content = content,
            Context = context,
            Importance = Math.Clamp(importance, 0, 1),
            Confidence = Math.Clamp(confidence, 0, 1),
            Embedding = embedding,
            Tags = ParseTags(tags),
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow,
            AccessCount = DefaultAccessCount,
            DecayFactor = DefaultDecayFactor
        };
    }

    private static List<string> ParseTags(string? tags)
    {
        return string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private async Task<List<SimilarMemory>> FindSimilarMemories(
        string userId,
        float[] embedding,
        MemoryCategory category,
        string? supersedes,
        CancellationToken ct)
    {
        var searchResults = await store.SearchAsync(
            userId,
            queryEmbedding: embedding,
            categories: [category],
            limit: SimilarMemorySearchLimit,
            ct: ct);

        return searchResults
            .Where(s => s.Relevance > SimilarityThreshold && s.Memory.Id != supersedes)
            .Select(s => new SimilarMemory(s.Memory.Id, s.Memory.Content, Math.Round(s.Relevance, 2)))
            .ToList();
    }

    private static JsonObject CreateCategoryErrorResponse(string category)
    {
        return new JsonObject
        {
            ["error"] =
                $"Invalid category: {category}. Valid: preference, fact, relationship, skill, project, personality, instruction"
        };
    }

    private static JsonObject CreateSuccessResponse(MemoryEntry memory, List<SimilarMemory> similarMemories)
    {
        var result = new JsonObject
        {
            ["status"] = "created",
            ["memoryId"] = memory.Id,
            ["userId"] = memory.UserId,
            ["category"] = memory.Category.ToString().ToLowerInvariant(),
            ["tier"] = memory.Tier.ToString().ToLowerInvariant()
        };

        if (similarMemories.Count <= 0)
        {
            return result;
        }

        result["similarMemories"] = new JsonArray(similarMemories.Select(s => s.ToJson()).ToArray());
        result["suggestion"] = "Similar memories found. Consider merging or using supersedes.";

        return result;
    }

    private sealed record SimilarMemory(string Id, string Content, double Similarity)
    {
        public JsonNode ToJson()
        {
            return new JsonObject
            {
                ["id"] = Id,
                ["content"] = Content,
                ["similarity"] = Similarity
            };
        }
    }
}