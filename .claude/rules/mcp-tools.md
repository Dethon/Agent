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

**Never write a call-tool filter by hand**, the same way you never hand-write an `fs_*` tool. Error
handling arrives with the hosting call, and every MCP server in the repo gets the same rule from the
same place in `Mcp.Hosting`. Do NOT add try/catch blocks in individual tool methods — exceptions
propagate to the filter, which logs and returns an error result.

- **Tool servers** get the filter from `AddToolServer(settings, ToolResponse.Create)`; **channel
  servers** get it from `AddChannelServer`. Both ask for one shared registration that installs at
  most once, so a **dual-role server** asking for both still ends up with a single filter (the first
  ask wins, which is the tool-server one).
- The rule it states, for every server: an `OperationCanceledException` propagates as the abort it
  is, because a cancelled call is a call somebody hung up on — a long poll when the agent
  disconnects, an `fs_exec` or a web fetch when it abandons the turn — and mapping that to an error
  result would hand the caller's pump something to retry on. Anything else becomes an error result.
- The error *shape* is the caller's: servers pass `errorResult: ToolResponse.Create` to keep their
  own envelope, which lives in Infrastructure and cannot be referenced from `Mcp.Hosting`.

## Key Points

- There is no MCP session: `McpServer.SessionId` is always null under the 2026-07-28 protocol, and
  `ClientInfo.Name` is the *agent* name, so it collapses every user and conversation into one bucket.
  Per-caller state is namespaced with `Domain.Channels.ConversationScope`, which reads the
  `ConversationContext` the agent stamps into every `tools/call`'s `_meta`. Never fall back when it
  is absent — return a `ToolError`, because a shared-bucket fallback leaks state across conversations
  and a per-request fallback silently severs multi-call flows.
- Do NOT add try/catch or `ILogger<T>` for error handling — the global filter handles this
- `Name` and `Description` constants come from the base Domain tool
