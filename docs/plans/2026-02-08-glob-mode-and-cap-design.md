# GlobFiles Tool: Mode Parameter and Result Cap

## Problem

The `GlobFiles` tool replaced the old `ListDirectories`/`ListFiles` tools but lacks their incremental browsing behavior. Agents use `**/*` patterns on large library directories, returning thousands of file paths and wasting tokens.

## Solution

Add a `mode` parameter to `GlobFiles` with two values:

- **`directories`** (default): Returns distinct directory paths matching the pattern. No result cap. Used for exploring library structure.
- **`files`**: Returns file paths matching the pattern. Capped at 200 results. If truncated, includes total count and a message telling the agent to narrow its pattern.

## Design

### Domain Layer

**New enum** — `Domain/Tools/Files/GlobMode.cs`:

```csharp
public enum GlobMode { Directories, Files }
```

**`GlobFilesTool.cs`** — Add `mode` parameter (default `Directories`):

- `Directories` mode: calls `IFileSystemClient.GlobDirectories(basePath, pattern, ct)` — returns distinct directory paths
- `Files` mode: calls existing `GlobFiles`, caps at 200 results. If truncated, returns:
  ```json
  { "files": [...], "truncated": true, "total": 1247, "message": "Showing 200 of 1247 matches. Use a more specific pattern." }
  ```
- When not truncated, returns plain JSON array (current behavior)

**`IFileSystemClient`** — Add method:

```csharp
Task<string[]> GlobDirectories(string basePath, string pattern, CancellationToken cancellationToken = default);
```

### Infrastructure Layer

**`LocalFileSystemClient`** — Implement `GlobDirectories`:

- Use `Matcher` to find all matching files, then extract distinct parent directory paths from the results.

### MCP Tool Wrappers

**`McpGlobFilesTool.cs`** and **`McpTextGlobFilesTool.cs`** — Add `mode` parameter:

```csharp
[Description("Search mode: 'directories' (default) lists matching directories for exploration, 'files' lists matching files (capped at 200 results).")]
string mode = "directories"
```

### Tool Description Update

Update `GlobFilesTool.Description` to:

```
Searches for files or directories matching a glob pattern relative to the library root.
Supports * (single segment), ** (recursive), and ? (single char).
Use mode 'directories' (default) to explore the library structure first, then 'files' with specific patterns to find content.
In files mode, results are capped at 200—use more specific patterns if truncated.
```

### Prompt Updates

**`KnowledgeBasePrompt.cs`:**
- Update tool description to mention mode parameter
- Change exploration guidance: use `GlobFiles` with directories mode to see structure, then files mode for specific content

**`DownloaderPrompt.cs`:**
- Replace `GlobFiles **/*` survey guidance with: use directories mode first to understand structure, then files mode with targeted patterns for organizing

### Tests

**`GlobFilesToolTests.cs`** — Add tests for:

- Directories mode calls `GlobDirectories` and returns JSON array
- Files mode calls `GlobFiles` and returns JSON array when under cap
- Files mode truncates at 200 and returns object with `truncated`, `total`, `message`
- Default mode is directories (no mode param = directories behavior)

## Constants

| Setting | Value |
|---------|-------|
| Default mode | `Directories` |
| File result cap | 200 |

## Files to Change

| File | Change |
|------|--------|
| `Domain/Tools/Files/GlobMode.cs` | New enum |
| `Domain/Tools/Files/GlobFilesTool.cs` | Add mode param, truncation logic |
| `Domain/Contracts/IFileSystemClient.cs` | Add `GlobDirectories` method |
| `Infrastructure/Clients/LocalFileSystemClient.cs` | Implement `GlobDirectories` |
| `McpServerLibrary/McpTools/McpGlobFilesTool.cs` | Add mode param |
| `McpServerText/McpTools/McpTextGlobFilesTool.cs` | Add mode param |
| `Domain/Prompts/KnowledgeBasePrompt.cs` | Update tool guidance |
| `Domain/Prompts/DownloaderPrompt.cs` | Update survey guidance |
| `Tests/Unit/Tools/GlobFilesToolTests.cs` | Add mode and truncation tests |
