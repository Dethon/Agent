using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Memory;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerMemory.McpTools;

[McpServerToolType]
public class McpMemoryReflectTool(IMemoryStore store)
    : MemoryReflectTool(store)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("User identifier")] string userId,
        [Description("Include source memories in response. Default: false")]
        bool includeMemories = false,
        CancellationToken cancellationToken = default)
    {
        var result = await Run(userId, includeMemories, cancellationToken);
        return ToolResponse.Create(result);
    }
}