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
public class McpExampleTool(IDependency dep) : ExampleTool(dep)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        string parameter,
        CancellationToken cancellationToken)
    {
        var sessionId = context.Server.StateKey;
        return ToolResponse.Create(await Run(sessionId, parameter, cancellationToken));
    }
}
```

## Error Handling

Error handling is centralized via `AddCallToolFilter` in each server's `ConfigModule.cs`. Do NOT add try/catch blocks in individual tool methods — exceptions propagate to the global filter which logs and returns `ToolResponse.Create(ex)`.

## Key Points

- Use `context.Server.StateKey` for session identification
- Do NOT add try/catch or `ILogger<T>` for error handling — the global filter handles this
- `Name` and `Description` constants come from the base Domain tool
