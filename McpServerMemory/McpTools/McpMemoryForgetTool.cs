using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Memory;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerMemory.McpTools;

[McpServerToolType]
public class McpMemoryForgetTool(IMemoryStore store, ILogger<McpMemoryForgetTool> logger)
    : MemoryForgetTool(store)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("User identifier for scoping memories")]
        string userId,
        [Description("Specific memory ID to forget (optional if using query)")]
        string? memoryId = null,
        [Description("Forget memories matching this content query (optional if using memoryId)")]
        string? query = null,
        [Description("Filter scope to categories (comma-separated)")]
        string? categories = null,
        [Description("Forget memories older than this ISO date")]
        string? olderThan = null,
        [Description("Mode: delete (permanent), archive (hide from recall). Default: delete")]
        string mode = "delete",
        [Description("Reason for forgetting (for audit)")]
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Run(userId, memoryId, query, categories, olderThan, mode, reason, cancellationToken);
            return ToolResponse.Create(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in {ToolName}", Name);
            return ToolResponse.Create(ex);
        }
    }
}