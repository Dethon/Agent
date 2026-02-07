using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Memory;

public class MemoryForgetTool(IMemoryStore store)
{
    private const int ContentPreviewLength = 100;

    protected const string Name = "memory_forget";

    protected const string Description = """
                                         Removes or archives memories. Use when information is outdated, wrong, or user
                                         explicitly asks you to forget something.

                                         Modes:
                                         - delete: Permanent removal
                                         - archive: Keep for history but exclude from normal recall (marks as superseded)

                                         When to forget:
                                         - User corrects previous information
                                         - User explicitly requests forgetting
                                         - Information is clearly outdated

                                         TIP: When user provides corrected info, prefer using memory_store with supersedes
                                         parameter instead—this preserves history while updating the active memory.
                                         """;

    protected async Task<JsonNode> Run(
        string userId,
        string? memoryId = null,
        string? query = null,
        string? categories = null,
        string? olderThan = null,
        string mode = "delete",
        string? reason = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memoryId) && string.IsNullOrWhiteSpace(query))
        {
            return CreateErrorResponse("Either memoryId or query must be provided");
        }

        var filter = new MemoryFilter(
            Query: query,
            Categories: ParseCategories(categories),
            OlderThan: ParseDate(olderThan));

        var affectedMemories = !string.IsNullOrWhiteSpace(memoryId)
            ? await ForgetById(userId, memoryId, mode, ct)
            : await ForgetByFilter(userId, filter, mode, ct);

        return CreateSuccessResponse(mode, affectedMemories, reason);
    }

    private async Task<List<AffectedMemory>> ForgetById(
        string userId,
        string memoryId,
        string mode,
        CancellationToken ct)
    {
        var memory = await store.GetByIdAsync(userId, memoryId, ct);
        if (memory is null)
        {
            return [];
        }

        var success = await ApplyForgetMode(userId, memory, mode, ct);
        return success
            ? [new AffectedMemory(memory.Id, memory.Content)]
            : [];
    }

    private async Task<List<AffectedMemory>> ForgetByFilter(
        string userId,
        MemoryFilter filter,
        string mode,
        CancellationToken ct)
    {
        var allMemories = await store.GetByUserIdAsync(userId, ct);
        var affected = new List<AffectedMemory>();

        foreach (var memory in allMemories.Where(filter.Matches))
        {
            if (await ApplyForgetMode(userId, memory, mode, ct))
            {
                affected.Add(new AffectedMemory(memory.Id, TruncateContent(memory.Content)));
            }
        }

        return affected;
    }

    private async Task<bool> ApplyForgetMode(string userId, MemoryEntry memory, string mode, CancellationToken ct)
    {
        return mode.ToLowerInvariant() switch
        {
            "delete" => await store.DeleteAsync(userId, memory.Id, ct),
            "archive" => await store.SupersedeAsync(userId, memory.Id, "archived", ct),
            _ => throw new ArgumentException($"Invalid mode: {mode}. Valid: delete, archive")
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

    private static DateTimeOffset? ParseDate(string? date)
    {
        return string.IsNullOrWhiteSpace(date) ? null : DateTimeOffset.Parse(date);
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

    private static JsonObject CreateSuccessResponse(string mode, List<AffectedMemory> affected, string? reason)
    {
        var response = new JsonObject
        {
            ["status"] = "success",
            ["action"] = mode,
            ["affectedCount"] = affected.Count,
            ["affectedMemories"] = new JsonArray(affected.Select(m => m.ToJson()).ToArray())
        };

        if (!string.IsNullOrWhiteSpace(reason))
        {
            response["reason"] = reason;
        }

        return response;
    }

    private sealed record MemoryFilter(
        string? Query,
        List<MemoryCategory>? Categories,
        DateTimeOffset? OlderThan)
    {
        public bool Matches(MemoryEntry memory)
        {
            if (!string.IsNullOrWhiteSpace(Query) &&
                !memory.Content.Contains(Query, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (Categories is not null && !Categories.Contains(memory.Category))
            {
                return false;
            }

            return !OlderThan.HasValue || memory.CreatedAt < OlderThan.Value;
        }
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