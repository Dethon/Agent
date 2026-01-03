using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Memory;

public class MemoryListTool(IMemoryStore store)
{
    private const int MinPageSize = 1;
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 20;

    protected const string Name = "memory_list";

    protected const string Description = """
                                         Lists memories with filtering, sorting, and pagination. Use this to review what
                                         you know about a user or find specific memories to update/delete.

                                         Use cases:
                                         - Audit what you've stored: no filters, sortBy=created
                                         - Find old memories: sortBy=accessed, order=asc
                                         - Review category: category=project to see all projects
                                         - Find important memories: sortBy=importance, order=desc

                                         The stats field shows distribution of memories.
                                         """;

    protected async Task<JsonNode> Run(
        string userId,
        string? category = null,
        string? tier = null,
        string sortBy = "created",
        string order = "desc",
        int page = 1,
        int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        var pagination = new PaginationParams(page, pageSize);
        var filter = new ListFilter(
            Category: ParseCategory(category),
            Tier: ParseTier(tier));
        var sorting = new SortParams(sortBy, order);

        var allMemories = await store.GetByUserIdAsync(userId, ct);
        var activeMemories = GetActiveMemories(allMemories, filter, sorting);

        var pagedResult = ApplyPagination(activeMemories, pagination);
        var stats = await store.GetStatsAsync(userId, ct);

        return CreateResponse(userId, pagedResult, stats);
    }

    private static List<MemoryEntry> GetActiveMemories(
        IReadOnlyList<MemoryEntry> memories,
        ListFilter filter,
        SortParams sorting)
    {
        var active = memories.Where(m => m.SupersededById is null);

        if (filter.Category.HasValue)
        {
            active = active.Where(m => m.Category == filter.Category.Value);
        }

        if (filter.Tier.HasValue)
        {
            active = active.Where(m => m.Tier == filter.Tier.Value);
        }

        return ApplySorting(active, sorting).ToList();
    }

    private static IEnumerable<MemoryEntry> ApplySorting(IEnumerable<MemoryEntry> memories, SortParams sorting)
    {
        return sorting.Field switch
        {
            SortField.Accessed => sorting.Descending
                ? memories.OrderByDescending(m => m.LastAccessedAt)
                : memories.OrderBy(m => m.LastAccessedAt),
            SortField.Importance => sorting.Descending
                ? memories.OrderByDescending(m => m.Importance)
                : memories.OrderBy(m => m.Importance),
            _ => sorting.Descending
                ? memories.OrderByDescending(m => m.CreatedAt)
                : memories.OrderBy(m => m.CreatedAt)
        };
    }

    private static PagedResult ApplyPagination(List<MemoryEntry> memories, PaginationParams pagination)
    {
        var paged = memories
            .Skip((pagination.Page - 1) * pagination.PageSize)
            .Take(pagination.PageSize)
            .ToList();

        return new PagedResult(
            Memories: paged,
            TotalItems: memories.Count,
            TotalPages: (int)Math.Ceiling(memories.Count / (double)pagination.PageSize),
            Page: pagination.Page,
            PageSize: pagination.PageSize);
    }

    private static MemoryCategory? ParseCategory(string? category)
    {
        return !string.IsNullOrWhiteSpace(category) &&
               Enum.TryParse<MemoryCategory>(category, ignoreCase: true, out var cat)
            ? cat
            : null;
    }

    private static MemoryTier? ParseTier(string? tier)
    {
        return tier?.ToLowerInvariant() switch
        {
            "long-term" => MemoryTier.LongTerm,
            "mid-term" => MemoryTier.MidTerm,
            _ => null
        };
    }

    private static JsonObject CreateResponse(string userId, PagedResult result, MemoryStats stats)
    {
        return new JsonObject
        {
            ["userId"] = userId,
            ["memories"] = CreateMemoriesJson(result.Memories),
            ["pagination"] = CreatePaginationJson(result),
            ["stats"] = CreateStatsJson(stats)
        };
    }

    private static JsonArray CreateMemoriesJson(List<MemoryEntry> memories)
    {
        return new JsonArray(memories.Select(m => (JsonNode)new JsonObject
        {
            ["id"] = m.Id,
            ["category"] = m.Category.ToString().ToLowerInvariant(),
            ["tier"] = m.Tier.ToString().ToLowerInvariant(),
            ["content"] = m.Content,
            ["importance"] = m.Importance,
            ["createdAt"] = m.CreatedAt.ToString("o"),
            ["lastAccessedAt"] = m.LastAccessedAt.ToString("o"),
            ["accessCount"] = m.AccessCount
        }).ToArray());
    }

    private static JsonObject CreatePaginationJson(PagedResult result)
    {
        return new JsonObject
        {
            ["page"] = result.Page,
            ["pageSize"] = result.PageSize,
            ["totalItems"] = result.TotalItems,
            ["totalPages"] = result.TotalPages
        };
    }

    private static JsonObject CreateStatsJson(MemoryStats stats)
    {
        var byCategory = new JsonObject();
        foreach (var (cat, count) in stats.ByCategory)
        {
            byCategory[cat.ToString().ToLowerInvariant()] = count;
        }

        var byTier = new JsonObject();
        foreach (var (tier, count) in stats.ByTier)
        {
            byTier[tier.ToString().ToLowerInvariant()] = count;
        }

        return new JsonObject
        {
            ["byCategory"] = byCategory,
            ["byTier"] = byTier
        };
    }

    private enum SortField { Created, Accessed, Importance }

    private sealed record ListFilter(MemoryCategory? Category, MemoryTier? Tier);

    private sealed record SortParams
    {
        public SortField Field { get; }
        public bool Descending { get; }

        public SortParams(string sortBy, string order)
        {
            Field = sortBy.ToLowerInvariant() switch
            {
                "accessed" => SortField.Accessed,
                "importance" => SortField.Importance,
                _ => SortField.Created
            };
            Descending = order.Equals("desc", StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record PaginationParams
    {
        public int Page { get; }
        public int PageSize { get; }

        public PaginationParams(int page, int pageSize)
        {
            Page = Math.Max(1, page);
            PageSize = Math.Clamp(pageSize, MinPageSize, MaxPageSize);
        }
    }

    private sealed record PagedResult(
        List<MemoryEntry> Memories,
        int TotalItems,
        int TotalPages,
        int Page,
        int PageSize);
}