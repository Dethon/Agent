---
paths: McpServer*/McpTools/*.cs
---

# MCP Tool Rules

MCP tools wrap Domain tools and expose them via Model Context Protocol.

## Structure

Each MCP tool should:
1. Inherit from the corresponding Domain tool
2. Use `[McpServerToolType]` class attribute
3. Use `[McpServerTool]` and `[Description]` method attributes
4. Return `CallToolResult` via `ToolResponse.Create()`

## Pattern

```csharp
[McpServerToolType]
public class McpExampleTool(IDependency dep, ILogger<McpExampleTool> logger)
    : ExampleTool(dep)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        string parameter,
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = context.Server.StateKey;
            return ToolResponse.Create(await Run(sessionId, parameter, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in {ToolName} tool", Name);
            return ToolResponse.Create(ex);
        }
    }
}
```

## Key Points

- Use `context.Server.StateKey` for session identification
- Wrap all calls in try/catch returning `ToolResponse.Create(ex)` on failure
- Log errors with tool name context
- `Name` and `Description` constants come from the base Domain tool
