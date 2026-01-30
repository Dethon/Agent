# MCP Tool Deduplication Design

## Problem

Two areas of duplicated logic across MCP server projects:

1. **30 MCP tool classes** each contain identical try/catch error handling wrapping `ToolResponse.Create()` calls, with inconsistent logging (some use `logger.IsEnabled(LogLevel.Error)` guards, some don't).

2. **`McpServerExtensions.StateKey`** is copy-pasted identically in `McpServerLibrary/Extensions/` and `McpServerWebSearch/Extensions/`.

## Solution

### 1. Global CallToolFilter for error handling

The MCP C# SDK provides `AddCallToolFilter` — a middleware pipeline for tool invocations. Register a single error-handling filter in each MCP server's `ConfigureMcp` method that catches exceptions globally, replacing all per-tool try/catch blocks.

**Add to each server's ConfigureMcp chain:**

```csharp
services
    .AddMcpServer()
    .WithHttpTransport()
    .AddCallToolFilter(next => async (context, cancellationToken) =>
    {
        try
        {
            return await next(context, cancellationToken);
        }
        catch (Exception ex)
        {
            var logger = context.Services?.GetRequiredService<ILogger<Program>>();
            logger?.LogError(ex, "Error in {ToolName} tool", context.Params?.Name);
            return ToolResponse.Create(ex);
        }
    })
    .WithTools<...>()
```

**Then simplify each MCP tool from:**

```csharp
[McpServerToolType]
public class McpListFilesTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath,
    ILogger<McpListFilesTool> logger) : ListFilesTool(client, libraryPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(string path, CancellationToken cancellationToken)
    {
        try
        {
            return ToolResponse.Create(await Run(path, cancellationToken));
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(ex, "Error in {ToolName} tool", Name);
            }
            return ToolResponse.Create(ex);
        }
    }
}
```

**To:**

```csharp
[McpServerToolType]
public class McpListFilesTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : ListFilesTool(client, libraryPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(string path, CancellationToken cancellationToken)
    {
        return ToolResponse.Create(await Run(path, cancellationToken));
    }
}
```

Remove `ILogger<T>` from tools that only used it for this catch block.

**Affected MCP servers (6):**
- McpServerLibrary — ConfigModule + 9 tools
- McpServerText — ConfigModule + 9 tools
- McpServerMemory — ConfigModule + 5 tools
- McpServerWebSearch — ConfigModule + 4 tools
- McpServerCommandRunner — ConfigModule + 2 tools
- McpServerIdealista — ConfigModule + 1 tool

### 2. Consolidate StateKey extension

Move the single `StateKey` extension to `Infrastructure/Extensions/McpServerExtensions.cs` and delete both project-local copies.

**Consumers to update `using` statements (12 call sites):**
- McpServerLibrary: 4 tool files + SubscriptionHandlers + ConfigModule
- McpServerWebSearch: 3 tool files
- Tests: 2 fixture files

**Files to delete:**
- `McpServerLibrary/Extensions/McpServerExtensions.cs`
- `McpServerWebSearch/Extensions/McpServerExtensions.cs`

## Implementation steps

### Step 1: Add StateKey to Infrastructure
- Create `Infrastructure/Extensions/McpServerExtensions.cs`
- Update `using` in all 12 consumer files
- Delete both duplicates

### Step 2: Add CallToolFilter to McpServerLibrary
- Add filter to `McpServerLibrary/Modules/ConfigModule.cs`
- Remove try/catch and unused `ILogger<T>` from all 9 tools
- Verify build

### Step 3: Add CallToolFilter to McpServerText
- Add filter to `McpServerText/Modules/ConfigModule.cs`
- Remove try/catch and unused `ILogger<T>` from all 9 tools
- Verify build

### Step 4: Add CallToolFilter to McpServerMemory
- Add filter to `McpServerMemory/Modules/ConfigModule.cs`
- Remove try/catch and unused `ILogger<T>` from all 5 tools
- Verify build

### Step 5: Add CallToolFilter to McpServerWebSearch
- Add filter to `McpServerWebSearch/Modules/ConfigModule.cs`
- Remove try/catch and unused `ILogger<T>` from all 4 tools
- Verify build

### Step 6: Add CallToolFilter to McpServerCommandRunner
- Add filter to `McpServerCommandRunner/Modules/ConfigModule.cs`
- Remove try/catch and unused `ILogger<T>` from both tools
- Verify build

### Step 7: Add CallToolFilter to McpServerIdealista
- Add filter to `McpServerIdealista/Modules/ConfigModule.cs`
- Remove try/catch and unused `ILogger<T>` from McpPropertySearchTool
- Verify build

### Step 8: Update mcp-tools coding rule
- Update `.claude/rules/mcp-tools.md` to reflect the new pattern (no try/catch in tools)

### Step 9: Final verification
- Build entire solution
- Run tests

## Notes

- Some tools use `ILogger<T>` for purposes beyond the catch block — only remove it when it's solely used for error handling in the try/catch.
- The `context.Params?.Name` in the filter gives the tool name, matching the current `Name` constant usage.
- The filter standardizes logging: no more inconsistency between guarded and unguarded log calls.
