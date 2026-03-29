using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Memory;

public class MemoryForgetTool(IMemoryStore store, IEmbeddingService embeddingService)
{
    private const int ContentPreviewLength = 100;
    private const int SearchLimit = 100;

    public const string Name = "memory_forget";

    public const string Description = """
                                         Removes or archives memories. Use when information is outdated, wrong, or user
                                         explicitly asks you to forget something.

                                         Modes:
                                         - delete: Permanent removal
                                         - archive: Keep for history but exclude from normal recall (marks as superseded)

                                         When to use:
                                         - User corrects previous information → archive the outdated memory
                                         - User explicitly requests forgetting
                                         - Information is clearly outdated
                                         - Bulk cleanup of low-importance memories

                                         TIP: When user provides corrected info, prefer using archive mode instead of
                                         delete—this preserves history while excluding the outdated memory from recall.
                                         Use semantic query (not exact text) to find memories — e.g. "my job" will match
                                         memories about employment.
                                         """;

    public async Task<JsonNode> Run(
        string userId,
        string? memoryId = null,
        string? query = null,
        string? categories = null,
        string? tags = null,
        string? olderThan = null,
        double? maxImportance = null,
        ForgetMode mode = ForgetMode.Delete,
        string? reason = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memoryId) && string.IsNullOrWhiteSpace(query))
        {
            return CreateErrorResponse("Either memoryId or query must be provided");
        }

        var affectedMemories = !string.IsNullOrWhiteSpace(memoryId)
            ? await ForgetById(userId, memoryId, mode, ct)
            : await ForgetBySearch(userId, query!, ParseCategories(categories), ParseTags(tags),
                ParseDate(olderThan), maxImportance, mode, ct);

        return CreateSuccessResponse(mode, affectedMemories, reason);
    }

    private async Task<List<AffectedMemory>> ForgetById(
        string userId, string memoryId, ForgetMode mode, CancellationToken ct)
    {
        var memory = await store.GetByIdAsync(userId, memoryId, ct);
        if (memory is null)
        {
            return [];
        }

        var success = await ApplyForgetMode(userId, memory, mode, ct);
        return success ? [new AffectedMemory(memory.Id, TruncateContent(memory.Content))] : [];
    }

    private async Task<List<AffectedMemory>> ForgetBySearch(
        string userId, string query, List<MemoryCategory>? parsedCategories, List<string>? parsedTags,
        DateTimeOffset? olderThan, double? maxImportance, ForgetMode mode, CancellationToken ct)
    {
        var queryEmbedding = await embeddingService.GenerateEmbeddingAsync(query, ct);

        var results = await store.SearchAsync(
            userId, query, queryEmbedding, parsedCategories, parsedTags,
            minImportance: null, limit: SearchLimit, ct);

        var affected = await Task.WhenAll(results
            .Where(r => (!olderThan.HasValue || r.Memory.CreatedAt < olderThan.Value)
                     && (!maxImportance.HasValue || r.Memory.Importance <= maxImportance.Value))
            .Select(async r =>
            {
                var success = await ApplyForgetMode(userId, r.Memory, mode, ct);
                return success ? new AffectedMemory(r.Memory.Id, TruncateContent(r.Memory.Content)) : null;
            }));

        return affected.OfType<AffectedMemory>().ToList();
    }

    private async Task<bool> ApplyForgetMode(string userId, MemoryEntry memory, ForgetMode mode, CancellationToken ct)
    {
        return mode switch
        {
            ForgetMode.Delete => await store.DeleteAsync(userId, memory.Id, ct),
            ForgetMode.Archive => await store.SupersedeAsync(userId, memory.Id, "archived", ct),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
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
        if (string.IsNullOrWhiteSpace(tags))
        {
            return null;
        }

        return tags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static DateTimeOffset? ParseDate(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
            return null;

        return DateTimeOffset.TryParse(date, out var result) ? result : null;
    }

    private static string TruncateContent(string content)
    {
        return content.Length > ContentPreviewLength
            ? content[..ContentPreviewLength] + "..."
            : content;
    }

    private static JsonObject CreateErrorResponse(string message)
    {
        return new JsonObject { ["error"] = message };
    }

    private static JsonObject CreateSuccessResponse(ForgetMode mode, List<AffectedMemory> affected, string? reason)
    {
        var response = new JsonObject
        {
            ["status"] = "success",
            ["action"] = mode.ToString().ToLowerInvariant(),
            ["affectedCount"] = affected.Count,
            ["affectedMemories"] = new JsonArray(affected.Select(m => m.ToJson()).ToArray())
        };

        if (!string.IsNullOrWhiteSpace(reason))
        {
            response["reason"] = reason;
        }

        return response;
    }

    private sealed record AffectedMemory(string Id, string Content)
    {
        public JsonNode ToJson()
        {
            return new JsonObject
            {
                ["id"] = Id,
                ["content"] = Content
            };
        }
    }
}
