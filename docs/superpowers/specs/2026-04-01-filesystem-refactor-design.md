# Filesystem Refactor: Virtual Filesystem Registry

## Summary

Refactor file system tools from MCP server tools into domain tools, with MCPs becoming pluggable filesystem backends. The agent owns the user-facing tools (read, edit, glob, etc.) and dispatches operations to filesystem MCPs via a standardized protocol. Path-based routing (`/library/...`, `/vault/...`) lets the LLM target different filesystems transparently.

## Goals

- File tools become `IDomainToolFeature` domain tools, configurable per agent via `enabledFeatures`
- Filesystem MCPs expose a standardized protocol of backend operations (`fs_read`, `fs_create`, etc.)
- A `VirtualFileSystemRegistry` maps virtual path prefixes to MCP backends
- Adding a new filesystem (vault, S3, archive) requires only deploying a new MCP — zero agent changes
- Each MCP can expose multiple filesystems via MCP resources

## Architecture Overview

```
LLM calls domain tool: text_read("/vault/notes/todo.md")
  → FileSystemToolFeature dispatches to TextReadTool
    → TextReadTool calls IVirtualFileSystemRegistry.Resolve("/vault/notes/todo.md")
      → Registry returns { McpClient, FilesystemName: "vault", RelativePath: "notes/todo.md" }
        → Tool calls fs_read(filesystem: "vault", path: "notes/todo.md") on the MCP client
          → MCP server reads the actual file, returns content
            → Tool formats the MCP response and returns it to the LLM
```

### Key Components

| Component | Layer | Location | Purpose |
|-----------|-------|----------|---------|
| `FileSystemToolFeature` | Domain | `Domain/Tools/FileSystem/` | `IDomainToolFeature` exposing file tools to the LLM |
| `IVirtualFileSystemRegistry` | Domain | `Domain/Contracts/` | Contract for resolving virtual paths to backends |
| `IFileSystemBackend` | Domain | `Domain/Contracts/` | Contract for invoking `fs_*` operations on a backend |
| `VirtualFileSystemRegistry` | Infrastructure | `Infrastructure/Agents/` | Maps mount points to MCP clients, handles discovery |
| `McpFileSystemBackend` | Infrastructure | `Infrastructure/Agents/` | `IFileSystemBackend` adapter over an `McpClient` |
| Filesystem MCP servers | MCP | `McpServer*/` | Standardized `fs_*` tools + `filesystem://` resources |

### Layer Boundaries

Following existing domain rules, the Domain layer defines contracts (`IVirtualFileSystemRegistry`, `IFileSystemBackend`) and contains tool logic. Infrastructure implements these by wrapping MCP clients. The domain tools never reference MCP types directly.

---

## Filesystem MCP Protocol

### Resources (Discovery)

Each filesystem MCP exposes one **MCP resource per filesystem** it provides. A single MCP can expose multiple filesystems.

Resource URI pattern: `filesystem://{name}`

Examples:
```
filesystem://library  →  { "name": "library", "mountPoint": "/library", "description": "Personal document library" }
filesystem://vault    →  { "name": "vault", "mountPoint": "/vault", "description": "Encrypted vault storage" }
```

Resource JSON schema:
```json
{
  "name": "string",
  "mountPoint": "string (must start with /)",
  "description": "string"
}
```

Discovery flow:
1. On MCP connect, `VirtualFileSystemRegistry` calls `client.ListResourcesAsync()`
2. Filters resources whose URI starts with `filesystem://`
3. Reads each matching resource to get the JSON metadata
4. Registers each `mountPoint → { mcpClient, filesystemName }`
5. If two MCPs claim the same mount point, the later one wins (last-write-wins, logged as warning)

### Tools (Operations)

Every filesystem MCP implements these standardized tools. All tools take a `filesystem` parameter (the name, e.g., `"library"`) so the MCP knows which filesystem to operate on. **The domain layer fills this parameter automatically** based on path resolution — the LLM never sees or sets it.

All paths in the MCP protocol are **relative to the filesystem's own root**. The domain layer strips the mount prefix before dispatching.

#### `fs_read`

Read file content with optional pagination.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filesystem` | string | yes | Filesystem name |
| `path` | string | yes | Relative path to file |
| `offset` | int? | no | Start from this line number (1-based, default: 1) |
| `limit` | int? | no | Max lines to return |

Returns:
```json
{
  "content": "1: first line\n2: second line\n...",
  "totalLines": 42,
  "truncated": false
}
```

When truncated:
```json
{
  "content": "...",
  "totalLines": 1500,
  "truncated": true,
  "nextOffset": 501
}
```

#### `fs_create`

Create a new file.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filesystem` | string | yes | Filesystem name |
| `path` | string | yes | Relative path for new file |
| `content` | string | yes | File content |
| `overwrite` | bool | no | Overwrite if exists (default: false) |
| `createDirectories` | bool | no | Create parent dirs (default: true) |

Returns:
```json
{
  "status": "created",
  "path": "notes/new-topic.md",
  "size": "1.2KB",
  "lines": 15
}
```

#### `fs_edit`

Edit file via exact string replacement.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filesystem` | string | yes | Filesystem name |
| `path` | string | yes | Relative path to file |
| `oldString` | string | yes | Exact text to find |
| `newString` | string | yes | Replacement text |
| `replaceAll` | bool | no | Replace all occurrences (default: false) |

Returns:
```json
{
  "status": "success",
  "path": "notes/todo.md",
  "occurrencesReplaced": 1,
  "affectedLines": { "start": 5, "end": 7 }
}
```

Errors:
- Text not found: `{ "error": "Text '...' not found in file." }`
- Ambiguous match: `{ "error": "Found 3 occurrences. Provide more context or set replaceAll=true." }`

#### `fs_glob`

Search for files or directories matching a glob pattern.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filesystem` | string | yes | Filesystem name |
| `basePath` | string | no | Relative base path (default: root) |
| `pattern` | string | yes | Glob pattern (`*`, `**`, `?`) |
| `mode` | string | yes | `"files"` or `"directories"` |

Returns (files mode):
```json
{
  "files": ["docs/readme.md", "docs/api.md"],
  "truncated": false,
  "total": 2
}
```

Returns (directories mode):
```json
{
  "directories": { "docs": ["readme.md", "api.md"], "src": ["main.cs"] }
}
```

#### `fs_search`

Search file contents with text or regex.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filesystem` | string | yes | Filesystem name |
| `query` | string | yes | Text or regex pattern |
| `regex` | bool | no | Treat query as regex (default: false) |
| `path` | string? | no | Search within single file |
| `directoryPath` | string | no | Directory to search in (default: root) |
| `filePattern` | string? | no | Glob filter (e.g., `"*.md"`) |
| `maxResults` | int | no | Max matches (default: 50) |
| `contextLines` | int | no | Context lines around match (default: 1) |
| `outputMode` | string | no | `"content"` or `"filesOnly"` (default: `"content"`) |

Returns: Same JSON structure as current `TextSearchTool` output.

#### `fs_move`

Move or rename a file/directory.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filesystem` | string | yes | Filesystem name |
| `sourcePath` | string | yes | Relative source path |
| `destinationPath` | string | yes | Relative destination path |

Returns:
```json
{
  "status": "success",
  "source": "old/path.md",
  "destination": "new/path.md"
}
```

#### `fs_delete`

Delete a file or directory (move to trash where supported).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filesystem` | string | yes | Filesystem name |
| `path` | string | yes | Relative path |

Returns:
```json
{
  "status": "success",
  "path": "old-notes/draft.md",
  "trashPath": ".trash/draft.md"
}
```

#### `fs_list`

List directory contents (flat, non-recursive).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filesystem` | string | yes | Filesystem name |
| `path` | string | no | Relative directory path (default: root) |

Returns:
```json
{
  "path": "docs",
  "entries": [
    { "name": "readme.md", "type": "file", "size": "2.1KB" },
    { "name": "api", "type": "directory" }
  ]
}
```

### Error Convention

All `fs_*` tools return errors as MCP tool error results. The domain tool layer translates these into user-friendly error strings for the LLM.

---

## Domain Tools (FileSystemToolFeature)

### Location

`Domain/Tools/FileSystem/` — new directory merging current `Domain/Tools/Text/` and `Domain/Tools/Files/`.

### Feature Registration

```csharp
public class FileSystemToolFeature(IVirtualFileSystemRegistry registry) : IDomainToolFeature
{
    public string FeatureName => "filesystem";

    public string? Prompt => BuildPrompt();

    public IEnumerable<AIFunction> GetTools(FeatureConfig config)
    {
        // Each tool is a (key, factory) pair; filter by EnabledTools if set
        var tools = new (string Key, Func<AIFunction> Factory)[]
        {
            ("read",   () => CreateTool<TextReadTool>("read", registry)),
            ("create", () => CreateTool<TextCreateTool>("create", registry)),
            ("edit",   () => CreateTool<TextEditTool>("edit", registry)),
            ("glob",   () => CreateTool<GlobFilesTool>("glob", registry)),
            ("search", () => CreateTool<TextSearchTool>("search", registry)),
            ("move",   () => CreateTool<MoveTool>("move", registry)),
            ("remove", () => CreateTool<RemoveTool>("remove", registry)),
            ("list",   () => CreateTool<ListTool>("list", registry)),
        };

        return tools
            .Where(t => config.EnabledTools is null || config.EnabledTools.Contains(t.Key))
            .Select(t => t.Factory());
    }
}
```

### Tool Internals — Dispatch Pattern

Each domain tool follows this pattern:

1. **Receive virtual path** from the LLM (e.g., `/vault/notes/todo.md`)
2. **Resolve** via `IVirtualFileSystemRegistry.Resolve(path)` → gets `{ Backend, RelativePath }`
3. **Call** the corresponding `IFileSystemBackend` method with the relative path
4. **Format** the backend response into the tool's return `JsonNode`

Example — `TextReadTool` after refactor:

```csharp
public class TextReadTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "read";
    public const string Name = "TextRead";

    public const string Description = """
        Reads a text file and returns its content with line numbers.
        Returns content formatted as "1: first line\n2: second line\n..."
        Large files are truncated — use offset and limit for pagination.

        Available filesystems: {filesystems}

        Parameters:
        - filePath: Virtual path (e.g., /library/notes/todo.md)
        - offset: Start from this line number (1-based, default: 1)
        - limit: Max lines to return
        """;

    public async Task<JsonNode> RunAsync(string filePath, int? offset = null, int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(filePath);
        return await resolution.Backend.ReadAsync(resolution.RelativePath, offset, limit, cancellationToken);
    }
}
```

Key changes from current tools:
- **No more `vaultPath` / `libraryPath` constructor params** — path resolution is entirely handled by the registry
- **No more `TextToolBase` / sandboxing in domain tools** — sandboxing is the MCP backend's responsibility (each filesystem knows its own root)
- **No more `IFileSystemClient` dependency** in domain tools — replaced by `IFileSystemBackend` via the registry
- **Async all the way** — domain tools call MCP backends over the network, so everything becomes `async Task<JsonNode>`

### Dynamic Tool Descriptions

Each tool's LLM description includes the list of available mount points so the LLM knows valid path prefixes. The `{filesystems}` placeholder is resolved at tool creation time from `registry.GetMounts()`:

```
Available filesystems:
- /library — Personal document library
- /vault — Encrypted vault storage
```

### Prompt

`FileSystemToolFeature.Prompt` returns a filesystem-specific system prompt listing available mount points and their descriptions, so the LLM has full context even before invoking a tool.

---

## Domain Contracts

### IVirtualFileSystemRegistry

```csharp
public interface IVirtualFileSystemRegistry
{
    /// Discover filesystems from filesystem MCP endpoints.
    /// Called during ThreadSession creation with all filesystem endpoints.
    Task DiscoverAsync(string[] endpoints, IFileSystemBackendFactory backendFactory, CancellationToken ct);

    /// Resolve a virtual path to a backend + relative path.
    /// Throws if no mount matches.
    FileSystemResolution Resolve(string virtualPath);

    /// All currently registered mount points.
    IReadOnlyList<FileSystemMount> GetMounts();
}

public record FileSystemResolution(IFileSystemBackend Backend, string RelativePath);

public record FileSystemMount(string Name, string MountPoint, string Description);
```

### IFileSystemBackend

Abstracts a single filesystem backend. Infrastructure implements this by wrapping MCP client tool calls.

```csharp
public interface IFileSystemBackend
{
    string FilesystemName { get; }

    Task<JsonNode> ReadAsync(string path, int? offset, int? limit, CancellationToken ct);
    Task<JsonNode> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct);
    Task<JsonNode> EditAsync(string path, string oldString, string newString, bool replaceAll, CancellationToken ct);
    Task<JsonNode> GlobAsync(string basePath, string pattern, string mode, CancellationToken ct);
    Task<JsonNode> SearchAsync(string query, bool regex, string? path, string? directoryPath, string? filePattern,
        int maxResults, int contextLines, string outputMode, CancellationToken ct);
    Task<JsonNode> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct);
    Task<JsonNode> DeleteAsync(string path, CancellationToken ct);
    Task<JsonNode> ListAsync(string path, CancellationToken ct);
}
```

### IFileSystemBackendFactory

Abstracts the creation of backends from MCP clients. Domain defines the contract; Infrastructure implements it.

```csharp
public interface IFileSystemBackendFactory
{
    /// Connect to an MCP endpoint and discover its filesystem resources.
    /// Returns one IFileSystemBackend per filesystem exposed by the MCP.
    Task<IReadOnlyList<(FileSystemMount Mount, IFileSystemBackend Backend)>> DiscoverAsync(
        string endpoint, CancellationToken ct);
}
```

---

## VirtualFileSystemRegistry (Infrastructure)

### Implementation

```csharp
internal sealed class VirtualFileSystemRegistry : IVirtualFileSystemRegistry
{
    // Mount point → backend, sorted by path length descending for longest-prefix matching
    private readonly SortedList<string, (FileSystemMount Mount, IFileSystemBackend Backend)> _mounts = new(...);

    public async Task DiscoverAsync(string[] endpoints, IFileSystemBackendFactory backendFactory, CancellationToken ct)
    {
        // For each endpoint, backendFactory connects to MCP, lists filesystem:// resources,
        // creates McpFileSystemBackend per filesystem
        foreach (var endpoint in endpoints)
        {
            var discovered = await backendFactory.DiscoverAsync(endpoint, ct);
            foreach (var (mount, backend) in discovered)
            {
                _mounts[mount.MountPoint] = (mount, backend);
            }
        }
    }

    public FileSystemResolution Resolve(string virtualPath)
    {
        // Find longest mount prefix that matches
        // e.g., "/vault/notes/todo.md" → mount "/vault", relative "notes/todo.md"
        var match = _mounts
            .Where(m => virtualPath.StartsWith(m.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.Key.Length)
            .Select(m => (FileSystemResolution?)new FileSystemResolution(
                m.Value.Backend,
                virtualPath[m.Key.Length..].TrimStart('/')))
            .FirstOrDefault();

        return match ?? throw new InvalidOperationException(
            $"No filesystem mounted for path '{virtualPath}'. Available: {FormatMounts()}");
    }

    public IReadOnlyList<FileSystemMount> GetMounts()
        => _mounts.Values.Select(v => v.Mount).ToList();

    private string FormatMounts()
        => string.Join(", ", _mounts.Values.Select(v => $"{v.Mount.MountPoint} ({v.Mount.Name})"));
}
```

### Thread Safety

The registry is created **per `ThreadSession`** (one per agent conversation thread), same as `McpClientManager`. No shared mutable state across threads. Discovery runs once during session creation, before any tool calls.

### Lifecycle — Integration with ThreadSessionBuilder

This is the key architectural change. Currently `ThreadSessionBuilder.BuildAsync`:

1. Creates MCP clients from `endpoints[]`
2. Lists all tools from all MCPs
3. Merges with domain tools

After refactor, endpoints are split into two categories:

```csharp
// In AgentDefinition
public required string[] McpServerEndpoints { get; init; }      // tool MCPs (websearch, idealista, etc.)
public string[] FileSystemEndpoints { get; init; } = [];         // filesystem MCPs (library, vault, etc.)
```

`ThreadSessionBuilder.BuildAsync` becomes:

1. Create MCP clients from `McpServerEndpoints` (tool MCPs — unchanged)
2. **Create `VirtualFileSystemRegistry`**, discover filesystems from `FileSystemEndpoints`:
   - For each endpoint, connect MCP client
   - List resources matching `filesystem://*`
   - Read resource metadata, create `McpFileSystemBackend` per filesystem
   - Register in registry
3. **Inject registry into `FileSystemToolFeature`** when building domain tools
4. Merge MCP tools + domain tools (filesystem domain tools now included)

Filesystem MCP tools (`fs_read`, `fs_edit`, etc.) are **never exposed to the LLM** — they are internal implementation details consumed by `McpFileSystemBackend`. Only the domain tools (`domain:filesystem:read`, etc.) are visible to the LLM.

### McpFileSystemBackend

```csharp
internal sealed class McpFileSystemBackend(McpClient client, string filesystemName) : IFileSystemBackend
{
    public string FilesystemName => filesystemName;

    public async Task<JsonNode> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        var result = await client.CallToolAsync("fs_read", new Dictionary<string, object?>
        {
            ["filesystem"] = filesystemName,
            ["path"] = path,
            ["offset"] = offset,
            ["limit"] = limit
        }, ct);

        return ParseToolResult(result);
    }

    // ... similar for all other operations
}
```

---

## Per-Agent Configuration

### Dotted Feature Names

Uses the existing `enabledFeatures` mechanism extended with dotted name support:

- `"filesystem"` — all file tools enabled
- `"filesystem.read"`, `"filesystem.move"` — only those specific tools

### DomainToolRegistry Changes

`GetToolsForFeatures` gains dotted name parsing. When processing `enabledFeatures`:

1. Group entries by feature name (part before first `.`, or the whole string if no `.`)
2. For bare names (no `.`): pass `EnabledTools = null` (all tools)
3. For dotted names: collect suffixes into a `HashSet<string>`, pass as `EnabledTools`

```csharp
public IEnumerable<AIFunction> GetToolsForFeatures(IEnumerable<string> enabledFeatures, FeatureConfig config)
{
    var grouped = enabledFeatures
        .Select(f => f.Split('.', 2))
        .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase);

    foreach (var group in grouped)
    {
        if (!_features.TryGetValue(group.Key, out var feature)) continue;

        var hasBare = group.Any(parts => parts.Length == 1);
        var enabledTools = hasBare
            ? null
            : group.Where(p => p.Length == 2).Select(p => p[1]).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var featureConfig = config with { EnabledTools = enabledTools };
        foreach (var tool in feature.GetTools(featureConfig))
            yield return tool;
    }
}
```

### FeatureConfig Extension

```csharp
public record FeatureConfig(
    IReadOnlySet<string>? EnabledTools = null,  // null = all tools in feature
    Func<SubAgentDefinition, DisposableAgent>? SubAgentFactory = null);
```

Existing features (scheduling, memory, subagents) ignore `EnabledTools` — they return all their tools regardless. This is backward-compatible.

### Whitelist Patterns

Filesystem domain tools follow the existing naming convention: `domain:filesystem:read`, `domain:filesystem:edit`, etc. Whitelist patterns work as before:

```json
"whitelistPatterns": [
    "domain:filesystem:*",
    "mcp:mcp-websearch:*"
]
```

### Configuration Examples

**Full access agent (after refactor):**
```json
{
    "id": "jonas",
    "name": "Jonas",
    "model": "z-ai/glm-5:nitro",
    "mcpServerEndpoints": [
        "http://mcp-websearch:8080/mcp",
        "http://mcp-idealista:8080/mcp"
    ],
    "fileSystemEndpoints": [
        "http://mcp-text:8080/mcp"
    ],
    "enabledFeatures": [
        "filesystem",
        "scheduling",
        "subagents",
        "memory"
    ],
    "whitelistPatterns": [
        "domain:filesystem:*",
        "domain:scheduling:*",
        "domain:subagents:*",
        "domain:memory:*",
        "mcp:mcp-websearch:*",
        "mcp:mcp-idealista:*"
    ]
}
```

**Restricted agent:**
```json
{
    "id": "cleaner",
    "name": "Cleaner",
    "model": "z-ai/glm-5:nitro",
    "fileSystemEndpoints": [
        "http://mcp-text:8080/mcp"
    ],
    "enabledFeatures": [
        "filesystem.move",
        "filesystem.remove"
    ],
    "whitelistPatterns": [
        "domain:filesystem:*"
    ]
}
```

**Agent with multiple filesystems:**
```json
{
    "id": "archivist",
    "name": "Archivist",
    "model": "z-ai/glm-5:nitro",
    "fileSystemEndpoints": [
        "http://mcp-text:8080/mcp",
        "http://mcp-vault:8080/mcp"
    ],
    "enabledFeatures": ["filesystem"],
    "whitelistPatterns": ["domain:filesystem:*"]
}
```

---

## McpServerText Transformation

McpServerText becomes the "library" filesystem backend MCP. It drops its current tool wrappers and implements the `fs_*` protocol.

### What Gets Removed

- `McpTools/McpTextReadTool.cs`
- `McpTools/McpTextEditTool.cs`
- `McpTools/McpTextCreateTool.cs`
- `McpTools/McpTextSearchTool.cs`
- `McpTools/McpTextGlobFilesTool.cs`
- `McpTools/McpMoveTool.cs`
- `McpTools/McpRemoveTool.cs`

### What Gets Added

- `McpTools/FsReadTool.cs` — wraps existing `TextReadTool` logic, takes `filesystem` param
- `McpTools/FsCreateTool.cs` — wraps `TextCreateTool` logic
- `McpTools/FsEditTool.cs` — wraps `TextEditTool` logic
- `McpTools/FsGlobTool.cs` — wraps `GlobFilesTool` logic
- `McpTools/FsSearchTool.cs` — wraps `TextSearchTool` logic
- `McpTools/FsMoveTool.cs` — wraps `MoveTool` logic
- `McpTools/FsDeleteTool.cs` — wraps `RemoveTool` logic
- `McpTools/FsListTool.cs` — new, lists directory contents
- `Resources/FileSystemResource.cs` — exposes `filesystem://library` resource

### Internal Structure

The `Fs*Tool` classes internally reuse the existing domain tool logic (or the `IFileSystemClient` / direct filesystem calls) to perform the actual operations. The `filesystem` parameter selects which root path to use (if the MCP exposes multiple filesystems).

For McpServerText specifically, the `filesystem` parameter will initially only accept `"library"`, with the root path being the configured `VaultPath`.

### Resource Registration

```csharp
[McpServerResourceType]
public class FileSystemResource(McpSettings settings)
{
    [McpServerResource(Name = "filesystem://library", Description = "Personal document library")]
    public string GetLibraryInfo()
    {
        return JsonSerializer.Serialize(new
        {
            name = "library",
            mountPoint = "/library",
            description = "Personal document library"
        });
    }
}
```

---

## Migration Path

### Phase 1: Infrastructure (no behavior change)

1. Add `IFileSystemBackend` and `IVirtualFileSystemRegistry` contracts to Domain
2. Add `IFileSystemBackendFactory` contract
3. Implement `VirtualFileSystemRegistry` and `McpFileSystemBackend` in Infrastructure
4. Add `FileSystemEndpoints` to `AgentDefinition` (optional field, default empty)
5. Extend `FeatureConfig` with `EnabledTools`
6. Update `DomainToolRegistry` to support dotted feature names

### Phase 2: Domain tools

1. Create `Domain/Tools/FileSystem/` directory
2. Refactor existing tools (`TextReadTool`, `TextEditTool`, `TextCreateTool`, `TextSearchTool`, `GlobFilesTool`, `MoveTool`, `RemoveTool`) to dispatch through `IVirtualFileSystemRegistry` instead of local filesystem access
3. Add new `ListTool`
4. Create `FileSystemToolFeature` implementing `IDomainToolFeature`
5. Add `FileSystemModule` in `Agent/Modules/`
6. Register in `ConfigModule.ConfigureAgents()`

### Phase 3: McpServerText becomes filesystem backend

1. Add `filesystem://library` resource to McpServerText
2. Replace `Mcp*Tool` wrappers with `Fs*Tool` implementations following the standardized protocol
3. Add `FsListTool` (new operation)
4. Update McpServerText's `ConfigModule` to register new tools and resource

### Phase 4: Configuration migration

1. Move `mcp-text` endpoint from `mcpServerEndpoints` to `fileSystemEndpoints` in appsettings
2. Update `whitelistPatterns`: replace `mcp:mcp-text:*` with `domain:filesystem:*`
3. Add `"filesystem"` to `enabledFeatures` for agents that had mcp-text
4. Update `docker-compose.yml` if any env vars changed
5. Remove old `Domain/Tools/Text/` and `Domain/Tools/Files/` after migration is complete
6. Remove `TextToolBase`, `LibraryPathConfig`, old `IFileSystemClient` (if no longer used)

### Phase 5: ThreadSessionBuilder integration

1. Update `ThreadSessionBuilder.BuildAsync` to handle `FileSystemEndpoints` separately
2. Create filesystem MCP clients → discover resources → build registry
3. Pass registry to `FileSystemToolFeature` via DI or constructor
4. Filesystem MCP tools are NOT added to the LLM-visible tool list

### Breaking Changes

- Agents using `mcp-text` in `mcpServerEndpoints` must move it to `fileSystemEndpoints`
- `whitelistPatterns` must be updated from `mcp:mcp-text:*` to `domain:filesystem:*`
- Tool names change: `mcp:mcp-text:TextRead` → `domain:filesystem:read` (affects any saved tool-approval preferences)

---

## Impact on Existing Components

| Component | Change |
|-----------|--------|
| `Domain/Tools/Files/*` | Refactored into `Domain/Tools/FileSystem/`, dispatch through registry |
| `Domain/Tools/Text/*` | Merged into `Domain/Tools/FileSystem/`, dispatch through registry |
| `Domain/Tools/Text/TextToolBase` | Removed — sandboxing moves to MCP backends |
| `Domain/Tools/Config/BaseLibraryPathConfig` | No longer needed by domain tools |
| `Domain/Contracts/IFileSystemClient` | Replaced by `IFileSystemBackend` for domain tool use; may remain for McpServerText internal use |
| `Domain/DTOs/AgentDefinition` | Gains `FileSystemEndpoints` field |
| `Domain/DTOs/FeatureConfig` | Gains `EnabledTools` property |
| `Infrastructure/Agents/DomainToolRegistry` | Supports dotted feature names |
| `Infrastructure/Agents/ThreadSession` | Builder handles filesystem endpoint discovery |
| `Infrastructure/Agents/MultiAgentFactory` | Passes filesystem endpoints through |
| `McpServerText` | Drops tool wrappers, implements `fs_*` backend protocol |
| `Agent/Modules/ConfigModule` | Adds `.AddFileSystem()` |
| `Agent/appsettings.json` | New `fileSystemEndpoints`, updated `enabledFeatures` and `whitelistPatterns` |

## New Filesystem MCP Template

To add a new filesystem (e.g., vault):

1. Create new MCP server project (e.g., `McpServerVault`)
2. Implement `fs_*` tools operating on vault storage
3. Expose `filesystem://vault` resource: `{ name: "vault", mountPoint: "/vault", description: "..." }`
4. Add to `docker-compose.yml`
5. Add endpoint to agent's `fileSystemEndpoints` in `appsettings.json`
6. The agent auto-discovers and mounts — no agent code changes needed
