using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Memory;

public class MemoryRecallTool(
    IMemoryStore store,
    IEmbeddingService embeddingService)
{
    private const int DefaultLimit = 10;

    protected const string Name = "memory_recall";

    protected const string Description = """
                                         **MANDATORY: Call this FIRST before responding to ANY user message.**

                                         Retrieves memories about the user including their preferences, personality profile,
                                         explicit instructions, and relevant context.

                                         Default call (ALWAYS do this first):
                                         memory_recall(userId, categories="preference,personality,instruction", limit=10)

                                         Additional searches when relevant:
                                         - Topic-specific: Add query parameter for semantic search
                                         - Skill context: categories="skill,fact" before giving advice

                                         Do NOT respond to users without first calling this tool.
                                         """;

    protected async Task<JsonNode> Run(
        string userId,
        string? query = null,
        string? categories = null,
        string? tags = null,
        double? minImportance = null,
        int limit = DefaultLimit,
        bool includeContext = false,
        CancellationToken ct = default)
    {
        var searchParams = new SearchParams(
            Query: query,
            Categories: ParseCategories(categories),
            Tags: ParseTags(tags),
            MinImportance: minImportance,
            Limit: limit);

        var queryEmbedding = await GetQueryEmbedding(query, ct);
        var results = await SearchMemories(userId, searchParams, queryEmbedding, ct);
        await UpdateAccessTimestamps(userId, results, ct);

        var profile = await store.GetProfileAsync(userId, ct);

        return CreateResponse(userId, query, results, profile, includeContext);
    }

    private async Task<float[]?> GetQueryEmbedding(string? query, CancellationToken ct)
    {
        return string.IsNullOrWhiteSpace(query)
            ? null
            : await embeddingService.GenerateEmbeddingAsync(query, ct);
    }

    private async Task<IReadOnlyList<MemorySearchResult>> SearchMemories(
        string userId,
        SearchParams searchParams,
        float[]? queryEmbedding,
        CancellationToken ct)
    {
        return await store.SearchAsync(
            userId,
            query: searchParams.Query,
            queryEmbedding: queryEmbedding,
            categories: searchParams.Categories,
            tags: searchParams.Tags,
            minImportance: searchParams.MinImportance,
            limit: searchParams.Limit,
            ct: ct);
    }

    private async Task UpdateAccessTimestamps(
        string userId,
        IReadOnlyList<MemorySearchResult> results,
        CancellationToken ct)
    {
        foreach (var result in results)
        {
            await store.UpdateAccessAsync(userId, result.Memory.Id, ct);
        }
    }

    private static List<MemoryCategory>? ParseCategories(string? categories)
    {
        if (string.IsNullOrWhiteSpace(categories))
        {
            return null;
        }

        return categories
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => Enum.TryParse<MemoryCategory>(c, ignoreCase: true, out var cat) ? cat : (MemoryCategory?)null)
            .Where(c => c.HasValue)
            .Select(c => c!.Value)
            .ToList();
    }

    private static List<string>? ParseTags(string? tags)
    {
        return string.IsNullOrWhiteSpace(tags)
            ? null
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static JsonObject CreateResponse(
        string userId,
        string? query,
        IReadOnlyList<MemorySearchResult> results,
        PersonalityProfile? profile,
        bool includeContext)
    {
        var response = new JsonObject
        {
            ["userId"] = userId,
            ["memories"] = CreateMemoriesJson(results, includeContext),
            ["totalMatches"] = results.Count
        };

        if (!string.IsNullOrWhiteSpace(query))
        {
            response["query"] = query;
        }

        if (profile is not null)
        {
            response["personalitySummary"] = profile.Summary;
        }

        return response;
    }

    private static JsonArray CreateMemoriesJson(IReadOnlyList<MemorySearchResult> results, bool includeContext)
    {
        return new JsonArray(results.Select(x => CreateMemoryJson(x, includeContext)).ToArray());
    }

    private static JsonNode CreateMemoryJson(MemorySearchResult result, bool includeContext)
    {
        var memory = result.Memory;
        var json = new JsonObject
        {
            ["id"] = memory.Id,
            ["category"] = memory.Category.ToString().ToLowerInvariant(),
            ["content"] = memory.Content,
            ["importance"] = memory.Importance,
            ["relevance"] = Math.Round(result.Relevance, 2),
            ["lastAccessed"] = memory.LastAccessedAt.ToString("o")
        };

        if (includeContext && !string.IsNullOrWhiteSpace(memory.Context))
        {
            json["context"] = memory.Context;
        }

        if (memory.Tags.Count > 0)
        {
            json["tags"] = new JsonArray(memory.Tags.Select(t => JsonValue.Create(t)).ToArray());
        }

        return json;
    }

    private sealed record SearchParams(
        string? Query,
        List<MemoryCategory>? Categories,
        List<string>? Tags,
        double? MinImportance,
        int Limit);
}