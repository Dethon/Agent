using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Memory;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerMemory.McpTools;

[McpServerToolType]
public class McpMemoryListTool(IMemoryStore store)
    : MemoryListTool(store)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("User identifier")] string userId,
        [Description(
            "Filter by single category: preference, fact, relationship, skill, project, personality, instruction")]
        string? category = null,
        [Description("Sort by: created, accessed, importance. Default: created")]
        string sortBy = "created",
        [Description("Sort order: asc or desc. Default: desc")]
        string order = "desc",
        [Description("Page number (1-based). Default: 1")]
        int page = 1,
        [Description("Items per page (max 100). Default: 20")]
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await Run(userId, category, sortBy, order, page, pageSize, cancellationToken);
        return ToolResponse.Create(result);
    }
}