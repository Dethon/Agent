# MCP Wrapper Template

MCP wrappers live in `McpServer*/McpTools/` and expose Domain tools via MCP.

## Template

```csharp
using System.ComponentModel;
using Domain.Tools.{Category};
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServer{Server}.McpTools;

[McpServerToolType]
public class Mcp{Name}Tool(
    IDependency dependency,
    ILogger<Mcp{Name}Tool> logger) : {Name}Tool(dependency)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        {parameters},
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = context.Server.StateKey;
            return ToolResponse.Create(await Run(sessionId, {args}, cancellationToken));
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

## Key Points

- Inherit from the Domain tool
- Use `[McpServerToolType]` on the class
- Use `[McpServerTool(Name = Name)]` and `[Description(Description)]` on the method
- Get session ID from `context.Server.StateKey`
- Wrap in try/catch, return `ToolResponse.Create(ex)` on error
- Log with tool name context using `{ToolName}` placeholder
- Check `logger.IsEnabled()` before logging to avoid allocation

## Example: MCP Wrapper

```csharp
using System.ComponentModel;
using Domain.Tools.Files;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class McpListFilesTool(
    IFileSystemClient fileSystem,
    ILogger<McpListFilesTool> logger) : ListFilesTool(fileSystem)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        string path,
        string? pattern,
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = context.Server.StateKey;
            return ToolResponse.Create(await Run(sessionId, path, pattern, cancellationToken));
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
