using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Memory;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerMemory.McpTools;

[McpServerToolType]
public class McpMemoryStoreTool(
    IMemoryStore store,
    IEmbeddingService embeddingService,
    ILogger<McpMemoryStoreTool> logger)
    : MemoryStoreTool(store, embeddingService)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("User identifier for scoping memories")]
        string userId,
        [Description("The memory content to store")]
        string content,
        [Description("Category: preference, fact, relationship, skill, project, personality, instruction")]
        string category,
        [Description("Importance score 0.0-1.0. Default: 0.5")]
        double importance = 0.5,
        [Description("Confidence in accuracy 0.0-1.0. Default: 0.7")]
        double confidence = 0.7,
        [Description("Searchable tags (comma-separated)")]
        string? tags = null,
        [Description("Context explaining how/when this was learned")]
        string? context = null,
        [Description("Memory ID this replaces (for updates)")]
        string? supersedes = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Run(userId, content, category, importance, confidence, tags, context, supersedes,
                cancellationToken);
            return ToolResponse.Create(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in {ToolName}", Name);
            return ToolResponse.Create(ex);
        }
    }
}