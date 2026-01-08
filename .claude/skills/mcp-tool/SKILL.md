---
name: mcp-tool
description: Create new MCP tools. Use when adding tools, creating MCP capabilities, extending server functionality, or adding new agent abilities.
allowed-tools: Read, Glob, Grep, Edit, Write
---

# Creating MCP Tools

MCP tools follow a two-layer pattern: pure Domain logic wrapped by an MCP server tool.

## Steps

1. **Domain Tool** - Create in `Domain/Tools/` with pure business logic
2. **MCP Wrapper** - Create in `McpServer*/McpTools/` inheriting from Domain tool
3. **Registration** - Add to DI in the MCP server's `Program.cs` or module

## Which MCP Server?

| Server | Purpose |
|--------|---------|
| `McpServerLibrary` | Torrent search, downloads, file organization |
| `McpServerText` | Text/markdown file inspection and editing |
| `McpServerWebSearch` | Web search and content fetching |
| `McpServerMemory` | Vector-based memory storage and recall |
| `McpServerCommandRunner` | CLI command execution |

## Templates

See [domain-template.md](domain-template.md) for the Domain tool pattern.
See [mcp-template.md](mcp-template.md) for the MCP wrapper pattern.

## Checklist

- [ ] Domain tool has `Name` and `Description` constants
- [ ] Domain tool contains pure logic (no MCP dependencies)
- [ ] MCP wrapper uses `[McpServerToolType]` class attribute
- [ ] MCP wrapper uses `[McpServerTool]` and `[Description]` method attributes
- [ ] Error handling returns `ToolResponse.Create(ex)` on failure
- [ ] Logging includes tool name context
- [ ] CancellationToken passed through all async operations
