using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Memory;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerMemory.McpTools;

[McpServerToolType]
public class McpMemoryRecallTool(
    IMemoryStore store,
    IEmbeddingService embeddingService)
    : MemoryRecallTool(store, embeddingService)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("User identifier for scoping memories")]
        string userId,
        [Description("Semantic search query (optional)")]
        string? query = null,
        [Description(
            "Filter by categories (comma-separated): preference, fact, relationship, skill, project, personality, instruction")]
        string? categories = null,
        [Description("Filter by tags (comma-separated, OR logic)")]
        string? tags = null,
        [Description("Minimum importance threshold 0.0-1.0")]
        double? minImportance = null,
        [Description("Maximum memories to return. Default: 10")]
        int limit = 10,
        [Description("Include storage context in response. Default: false")]
        bool includeContext = false,
        CancellationToken cancellationToken = default)
    {
        var result = await Run(userId, query, categories, tags, minImportance, limit, includeContext,
            cancellationToken);
        return ToolResponse.Create(result);
    }
}