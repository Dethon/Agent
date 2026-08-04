---
paths:
  - "McpServer*/McpTools/*.cs"
---

# MCP Tool Rules

MCP tools wrap Domain tools and expose them via Model Context Protocol.

**Filesystem tools are the exception: never write one.** An `fs_*` tool is registered by
`AddFileSystemTools<TBackend>()` (`Infrastructure/Utils/FileSystemServerTools.cs`) for exactly the
operations `TBackend` overrides on `FileSystemBackendBase`, and its description comes from that
backend's `Describe*` hook. Hand-writing one would let a server advertise an operation its backend
does not implement, which is the drift the registrar exists to make unrepresentable. See CLAUDE.md's
"Virtual Filesystem Architecture".

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
        if (!ConversationScope.TryResolve(context.Params?.Meta, out var scope))
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Conversation context is missing from request _meta.",
                retryable: false));
        }

        return ToolResponse.Create(await Run(scope, parameter, cancellationToken));
    }
}
```

The scope guard is only needed by tools that cache per-caller state across calls
(`file_search`/`download_file`, the `web_*` browse tools). A stateless tool takes no scope at all.

## Error Handling

Error handling is centralized in one call-tool filter. Do NOT add try/catch blocks in individual tool methods — exceptions propagate to the filter, which logs and returns an error result.

- **Channel servers** get the filter from `AddChannelServer` (`Mcp.Hosting`), which states the rule once: an `OperationCanceledException` propagates as the abort it is, because a long poll ends in cancellation whenever the agent hangs up, and mapping that to an error result would hand the pump something to retry on. Anything else becomes an error result. The two dual-role servers pass `errorResult: ToolResponse.Create` so they keep their own envelope shape, which lives in Infrastructure and cannot be referenced from `Mcp.Hosting`.
- **Non-channel servers** still register `AddCallToolFilter` in their own `ConfigModule.cs`, returning `ToolResponse.Create(ex)`.

## Key Points

- There is no MCP session: `McpServer.SessionId` is always null under the 2026-07-28 protocol, and
  `ClientInfo.Name` is the *agent* name, so it collapses every user and conversation into one bucket.
  Per-caller state is namespaced with `Domain.Channels.ConversationScope`, which reads the
  `ConversationContext` the agent stamps into every `tools/call`'s `_meta`. Never fall back when it
  is absent — return a `ToolError`, because a shared-bucket fallback leaks state across conversations
  and a per-request fallback silently severs multi-call flows.
- Do NOT add try/catch or `ILogger<T>` for error handling — the global filter handles this
- `Name` and `Description` constants come from the base Domain tool
