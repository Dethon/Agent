# Filesystem Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor file system tools from MCP server tools into domain tools, with MCPs becoming pluggable filesystem backends discovered via `filesystem://` resources and dispatched through a virtual filesystem registry.

**Architecture:** Domain tools (`Domain/Tools/FileSystem/`) own the LLM-facing interface. `IVirtualFileSystemRegistry` resolves virtual paths (`/library/...`, `/vault/...`) to `IFileSystemBackend` instances. Infrastructure implements backends by wrapping MCP client tool calls to standardized `fs_*` tools. `McpServerText` transforms from a tool-exposing MCP into a filesystem backend MCP. `DomainToolRegistry` gains dotted feature name support (`filesystem.read`, `filesystem.move`) for per-agent tool granularity.

**Tech Stack:** .NET 10, Microsoft.Extensions.AI, ModelContextProtocol (C# SDK), xUnit, Shouldly, Moq

**Spec:** `docs/superpowers/specs/2026-04-01-filesystem-refactor-design.md`

---

## File Structure

### Domain Layer — New/Modified

| File | Action | Responsibility |
|------|--------|----------------|
| `Domain/Contracts/IFileSystemBackend.cs` | Create | Contract for filesystem backend operations (read, create, edit, glob, search, move, delete, list) |
| `Domain/Contracts/IVirtualFileSystemRegistry.cs` | Create | Contract for resolving virtual paths to backends + mount discovery |
| `Domain/Contracts/IFileSystemBackendFactory.cs` | Create | Contract for creating backends from MCP endpoints |
| `Domain/DTOs/FileSystemMount.cs` | Create | Record: `Name`, `MountPoint`, `Description` |
| `Domain/DTOs/FileSystemResolution.cs` | Create | Record: `Backend`, `RelativePath` |
| `Domain/DTOs/FeatureConfig.cs` | Modify | Add `EnabledTools` property |
| `Domain/DTOs/AgentDefinition.cs` | Modify | Add `FileSystemEndpoints` property |
| `Domain/DTOs/SubAgentDefinition.cs` | Modify | Add `FileSystemEndpoints` property |
| `Domain/Tools/FileSystem/TextReadTool.cs` | Create | Dispatches to `IFileSystemBackend.ReadAsync` |
| `Domain/Tools/FileSystem/TextCreateTool.cs` | Create | Dispatches to `IFileSystemBackend.CreateAsync` |
| `Domain/Tools/FileSystem/TextEditTool.cs` | Create | Dispatches to `IFileSystemBackend.EditAsync` |
| `Domain/Tools/FileSystem/GlobFilesTool.cs` | Create | Dispatches to `IFileSystemBackend.GlobAsync` |
| `Domain/Tools/FileSystem/TextSearchTool.cs` | Create | Dispatches to `IFileSystemBackend.SearchAsync` |
| `Domain/Tools/FileSystem/MoveTool.cs` | Create | Dispatches to `IFileSystemBackend.MoveAsync` |
| `Domain/Tools/FileSystem/RemoveTool.cs` | Create | Dispatches to `IFileSystemBackend.DeleteAsync` |
| `Domain/Tools/FileSystem/ListTool.cs` | Create | Dispatches to `IFileSystemBackend.ListAsync` |
| `Domain/Tools/FileSystem/FileSystemToolFeature.cs` | Create | `IDomainToolFeature` that registers all filesystem tools |

### Infrastructure Layer — New/Modified

| File | Action | Responsibility |
|------|--------|----------------|
| `Infrastructure/Agents/VirtualFileSystemRegistry.cs` | Create | Implements `IVirtualFileSystemRegistry`: mount storage, longest-prefix resolution |
| `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs` | Create | Implements `IFileSystemBackend` by calling `fs_*` tools on an `McpClient` |
| `Infrastructure/Agents/Mcp/McpFileSystemBackendFactory.cs` | Create | Implements `IFileSystemBackendFactory`: connects to MCP, reads `filesystem://` resources, creates backends |
| `Infrastructure/Agents/DomainToolRegistry.cs` | Modify | Support dotted feature names with per-feature tool filtering |
| `Infrastructure/Agents/ThreadSession.cs` | Modify | Add filesystem endpoint discovery, pass registry to domain tools |
| `Infrastructure/Agents/McpAgent.cs` | Modify | Accept and pass `fileSystemEndpoints` |
| `Infrastructure/Agents/MultiAgentFactory.cs` | Modify | Pass `FileSystemEndpoints` through agent creation |

### McpServerText — New/Modified

| File | Action | Responsibility |
|------|--------|----------------|
| `McpServerText/McpTools/FsReadTool.cs` | Create | `fs_read` MCP tool |
| `McpServerText/McpTools/FsCreateTool.cs` | Create | `fs_create` MCP tool |
| `McpServerText/McpTools/FsEditTool.cs` | Create | `fs_edit` MCP tool |
| `McpServerText/McpTools/FsGlobTool.cs` | Create | `fs_glob` MCP tool |
| `McpServerText/McpTools/FsSearchTool.cs` | Create | `fs_search` MCP tool |
| `McpServerText/McpTools/FsMoveTool.cs` | Create | `fs_move` MCP tool |
| `McpServerText/McpTools/FsDeleteTool.cs` | Create | `fs_delete` MCP tool |
| `McpServerText/McpTools/FsListTool.cs` | Create | `fs_list` MCP tool |
| `McpServerText/McpResources/FileSystemResource.cs` | Create | `filesystem://library` resource |
| `McpServerText/McpTools/McpTextReadTool.cs` | Delete | Old tool wrapper |
| `McpServerText/McpTools/McpTextEditTool.cs` | Delete | Old tool wrapper |
| `McpServerText/McpTools/McpTextCreateTool.cs` | Delete | Old tool wrapper |
| `McpServerText/McpTools/McpTextSearchTool.cs` | Delete | Old tool wrapper |
| `McpServerText/McpTools/McpTextGlobFilesTool.cs` | Delete | Old tool wrapper |
| `McpServerText/McpTools/McpMoveTool.cs` | Delete | Old tool wrapper |
| `McpServerText/McpTools/McpRemoveTool.cs` | Delete | Old tool wrapper |
| `McpServerText/Modules/ConfigModule.cs` | Modify | Register new `Fs*Tool` classes and `FileSystemResource` |

### Agent Layer — Modified

| File | Action | Responsibility |
|------|--------|----------------|
| `Agent/Modules/FileSystemModule.cs` | Create | DI registration for `FileSystemToolFeature` |
| `Agent/Modules/ConfigModule.cs` | Modify | Add `.AddFileSystem()` call |
| `Agent/appsettings.json` | Modify | Move mcp-text to `fileSystemEndpoints`, add `"filesystem"` to `enabledFeatures` |

### Tests — New

| File | Action | Responsibility |
|------|--------|----------------|
| `Tests/Unit/Infrastructure/VirtualFileSystemRegistryTests.cs` | Create | Registry resolution, discovery, error handling |
| `Tests/Unit/Infrastructure/DomainToolRegistryDottedTests.cs` | Create | Dotted feature name parsing and filtering |
| `Tests/Unit/Domain/Tools/FileSystem/TextReadToolTests.cs` | Create | Read tool dispatches correctly |
| `Tests/Unit/Domain/Tools/FileSystem/TextCreateToolTests.cs` | Create | Create tool dispatches correctly |
| `Tests/Unit/Domain/Tools/FileSystem/TextEditToolTests.cs` | Create | Edit tool dispatches correctly |
| `Tests/Unit/Domain/Tools/FileSystem/GlobFilesToolTests.cs` | Create | Glob tool dispatches correctly |
| `Tests/Unit/Domain/Tools/FileSystem/TextSearchToolTests.cs` | Create | Search tool dispatches correctly |
| `Tests/Unit/Domain/Tools/FileSystem/MoveToolTests.cs` | Create | Move tool dispatches correctly |
| `Tests/Unit/Domain/Tools/FileSystem/RemoveToolTests.cs` | Create | Remove tool dispatches correctly |
| `Tests/Unit/Domain/Tools/FileSystem/ListToolTests.cs` | Create | List tool dispatches correctly |
| `Tests/Unit/Domain/Tools/FileSystem/FileSystemToolFeatureTests.cs` | Create | Feature registration, tool filtering by EnabledTools |

### Cleanup — Delete

| File | Action |
|------|--------|
| `Domain/Tools/Text/TextReadTool.cs` | Delete |
| `Domain/Tools/Text/TextEditTool.cs` | Delete |
| `Domain/Tools/Text/TextCreateTool.cs` | Delete |
| `Domain/Tools/Text/TextSearchTool.cs` | Delete |
| `Domain/Tools/Text/TextToolBase.cs` | Delete |
| `Domain/Tools/Text/SearchOutputMode.cs` | Delete |
| `Domain/Tools/Files/GlobFilesTool.cs` | Delete |
| `Domain/Tools/Files/MoveTool.cs` | Delete |
| `Domain/Tools/Files/RemoveTool.cs` | Delete |
| `Domain/Tools/Files/GlobMode.cs` | Delete |
| `Domain/Tools/Config/BaseLibraryPathConfig.cs` | Delete (if no other consumers) |

---

## Task 1: Domain Contracts and DTOs

**Files:**
- Create: `Domain/Contracts/IFileSystemBackend.cs`
- Create: `Domain/Contracts/IVirtualFileSystemRegistry.cs`
- Create: `Domain/Contracts/IFileSystemBackendFactory.cs`
- Create: `Domain/DTOs/FileSystemMount.cs`
- Create: `Domain/DTOs/FileSystemResolution.cs`
- Modify: `Domain/DTOs/FeatureConfig.cs`
- Modify: `Domain/DTOs/AgentDefinition.cs`
- Modify: `Domain/DTOs/SubAgentDefinition.cs`

- [ ] **Step 1: Create `IFileSystemBackend.cs`**

```csharp
using System.Text.Json.Nodes;

namespace Domain.Contracts;

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

- [ ] **Step 2: Create `FileSystemMount.cs` and `FileSystemResolution.cs`**

`Domain/DTOs/FileSystemMount.cs`:
```csharp
namespace Domain.DTOs;

public record FileSystemMount(string Name, string MountPoint, string Description);
```

`Domain/DTOs/FileSystemResolution.cs`:
```csharp
using Domain.Contracts;

namespace Domain.DTOs;

public record FileSystemResolution(IFileSystemBackend Backend, string RelativePath);
```

- [ ] **Step 3: Create `IVirtualFileSystemRegistry.cs`**

```csharp
using Domain.DTOs;

namespace Domain.Contracts;

public interface IVirtualFileSystemRegistry
{
    Task DiscoverAsync(string[] endpoints, IFileSystemBackendFactory backendFactory, CancellationToken ct);
    FileSystemResolution Resolve(string virtualPath);
    IReadOnlyList<FileSystemMount> GetMounts();
}
```

- [ ] **Step 4: Create `IFileSystemBackendFactory.cs`**

```csharp
using Domain.DTOs;

namespace Domain.Contracts;

public interface IFileSystemBackendFactory
{
    Task<IReadOnlyList<(FileSystemMount Mount, IFileSystemBackend Backend)>> DiscoverAsync(
        string endpoint, CancellationToken ct);
}
```

- [ ] **Step 5: Modify `FeatureConfig.cs`**

Replace the entire file content. The file is at `Domain/DTOs/FeatureConfig.cs`.

```csharp
using Domain.Agents;

namespace Domain.DTOs;

public record FeatureConfig(
    IReadOnlySet<string>? EnabledTools = null,
    Func<SubAgentDefinition, DisposableAgent>? SubAgentFactory = null);
```

- [ ] **Step 6: Modify `AgentDefinition.cs` — add `FileSystemEndpoints`**

Add after the `McpServerEndpoints` property (line 12):

```csharp
public string[] FileSystemEndpoints { get; init; } = [];
```

- [ ] **Step 7: Modify `SubAgentDefinition.cs` — add `FileSystemEndpoints`**

Add after the `McpServerEndpoints` property:

```csharp
public string[] FileSystemEndpoints { get; init; } = [];
```

- [ ] **Step 8: Verify the project builds**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded

- [ ] **Step 9: Commit**

```bash
git add Domain/Contracts/IFileSystemBackend.cs Domain/Contracts/IVirtualFileSystemRegistry.cs \
  Domain/Contracts/IFileSystemBackendFactory.cs Domain/DTOs/FileSystemMount.cs \
  Domain/DTOs/FileSystemResolution.cs Domain/DTOs/FeatureConfig.cs \
  Domain/DTOs/AgentDefinition.cs Domain/DTOs/SubAgentDefinition.cs
git commit -m "$(cat <<'EOF'
feat: add domain contracts and DTOs for virtual filesystem registry

Introduce IFileSystemBackend, IVirtualFileSystemRegistry, and
IFileSystemBackendFactory contracts. Add FileSystemMount and
FileSystemResolution DTOs. Extend FeatureConfig with EnabledTools
for dotted feature name support. Add FileSystemEndpoints to agent
definitions.
EOF
)"
```

---

## Task 2: DomainToolRegistry Dotted Feature Names

**Files:**
- Modify: `Infrastructure/Agents/DomainToolRegistry.cs`
- Create: `Tests/Unit/Infrastructure/DomainToolRegistryDottedTests.cs`

- [ ] **Step 1: Write failing tests for dotted feature name parsing**

Create `Tests/Unit/Infrastructure/DomainToolRegistryDottedTests.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class DomainToolRegistryDottedTests
{
    private readonly Mock<IDomainToolFeature> _filesystemFeature = new();
    private readonly Mock<IDomainToolFeature> _schedulingFeature = new();
    private readonly DomainToolRegistry _registry;

    public DomainToolRegistryDottedTests()
    {
        _filesystemFeature.Setup(f => f.FeatureName).Returns("filesystem");
        _schedulingFeature.Setup(f => f.FeatureName).Returns("scheduling");
        _registry = new DomainToolRegistry([_filesystemFeature.Object, _schedulingFeature.Object]);
    }

    [Fact]
    public void GetToolsForFeatures_BareFeatureName_PassesNullEnabledTools()
    {
        FeatureConfig? captured = null;
        _filesystemFeature
            .Setup(f => f.GetTools(It.IsAny<FeatureConfig>()))
            .Callback<FeatureConfig>(c => captured = c)
            .Returns([]);

        _registry.GetToolsForFeatures(["filesystem"], new FeatureConfig()).ToList();

        captured.ShouldNotBeNull();
        captured.EnabledTools.ShouldBeNull();
    }

    [Fact]
    public void GetToolsForFeatures_DottedNames_PassesToolFilter()
    {
        FeatureConfig? captured = null;
        _filesystemFeature
            .Setup(f => f.GetTools(It.IsAny<FeatureConfig>()))
            .Callback<FeatureConfig>(c => captured = c)
            .Returns([]);

        _registry.GetToolsForFeatures(["filesystem.read", "filesystem.move"], new FeatureConfig()).ToList();

        captured.ShouldNotBeNull();
        captured.EnabledTools.ShouldNotBeNull();
        captured.EnabledTools.Count.ShouldBe(2);
        captured.EnabledTools.ShouldContain("read");
        captured.EnabledTools.ShouldContain("move");
    }

    [Fact]
    public void GetToolsForFeatures_BareAndDottedMixed_BareWins()
    {
        FeatureConfig? captured = null;
        _filesystemFeature
            .Setup(f => f.GetTools(It.IsAny<FeatureConfig>()))
            .Callback<FeatureConfig>(c => captured = c)
            .Returns([]);

        _registry.GetToolsForFeatures(["filesystem", "filesystem.read"], new FeatureConfig()).ToList();

        captured.ShouldNotBeNull();
        captured.EnabledTools.ShouldBeNull();
    }

    [Fact]
    public void GetToolsForFeatures_DottedNames_CaseInsensitive()
    {
        FeatureConfig? captured = null;
        _filesystemFeature
            .Setup(f => f.GetTools(It.IsAny<FeatureConfig>()))
            .Callback<FeatureConfig>(c => captured = c)
            .Returns([]);

        _registry.GetToolsForFeatures(["FileSystem.Read", "FILESYSTEM.Move"], new FeatureConfig()).ToList();

        captured.ShouldNotBeNull();
        captured.EnabledTools.ShouldNotBeNull();
        captured.EnabledTools.ShouldContain("Read");
        captured.EnabledTools.ShouldContain("Move");
    }

    [Fact]
    public void GetToolsForFeatures_UnknownFeature_SkipsGracefully()
    {
        var tools = _registry.GetToolsForFeatures(["nonexistent.read"], new FeatureConfig()).ToList();
        tools.ShouldBeEmpty();
    }

    [Fact]
    public void GetToolsForFeatures_PreservesSubAgentFactory()
    {
        FeatureConfig? captured = null;
        _filesystemFeature
            .Setup(f => f.GetTools(It.IsAny<FeatureConfig>()))
            .Callback<FeatureConfig>(c => captured = c)
            .Returns([]);

        Func<SubAgentDefinition, DisposableAgent> factory = _ => null!;
        var config = new FeatureConfig(SubAgentFactory: factory);

        _registry.GetToolsForFeatures(["filesystem.read"], config).ToList();

        captured.ShouldNotBeNull();
        captured.SubAgentFactory.ShouldBe(factory);
    }

    [Fact]
    public void GetPromptsForFeatures_DottedNames_ResolvesToFeature()
    {
        _filesystemFeature.Setup(f => f.Prompt).Returns("filesystem prompt");

        var prompts = _registry.GetPromptsForFeatures(["filesystem.read"]).ToList();

        prompts.Count.ShouldBe(1);
        prompts[0].ShouldBe("filesystem prompt");
    }

    [Fact]
    public void GetPromptsForFeatures_DuplicateFeatureFromMultipleDots_ReturnsPromptOnce()
    {
        _filesystemFeature.Setup(f => f.Prompt).Returns("filesystem prompt");

        var prompts = _registry.GetPromptsForFeatures(["filesystem.read", "filesystem.move"]).ToList();

        prompts.Count.ShouldBe(1);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~DomainToolRegistryDottedTests" --no-restore`
Expected: FAIL — current `GetToolsForFeatures` doesn't understand dotted names

- [ ] **Step 3: Implement dotted feature name support in `DomainToolRegistry.cs`**

Replace the entire content of `Infrastructure/Agents/DomainToolRegistry.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents;

public class DomainToolRegistry(IEnumerable<IDomainToolFeature> features) : IDomainToolRegistry
{
    private readonly Dictionary<string, IDomainToolFeature> _features =
        features.ToDictionary(f => f.FeatureName, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<AIFunction> GetToolsForFeatures(IEnumerable<string> enabledFeatures, FeatureConfig config)
    {
        return GroupByFeature(enabledFeatures)
            .Where(g => _features.ContainsKey(g.FeatureName))
            .SelectMany(g =>
            {
                var featureConfig = config with { EnabledTools = g.EnabledTools };
                return _features[g.FeatureName].GetTools(featureConfig);
            });
    }

    public IEnumerable<string> GetPromptsForFeatures(IEnumerable<string> enabledFeatures)
    {
        return GroupByFeature(enabledFeatures)
            .Where(g => _features.ContainsKey(g.FeatureName))
            .Select(g => _features[g.FeatureName].Prompt)
            .OfType<string>();
    }

    private static IEnumerable<FeatureGroup> GroupByFeature(IEnumerable<string> enabledFeatures)
    {
        return enabledFeatures
            .Select(f => f.Split('.', 2))
            .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var hasBare = group.Any(parts => parts.Length == 1);
                var enabledTools = hasBare
                    ? null
                    : group
                        .Where(p => p.Length == 2)
                        .Select(p => p[1])
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return new FeatureGroup(group.Key, enabledTools);
            });
    }

    private record FeatureGroup(string FeatureName, IReadOnlySet<string>? EnabledTools);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~DomainToolRegistryDottedTests" --no-restore`
Expected: All 8 tests PASS

- [ ] **Step 5: Run existing tests to verify no regressions**

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E" --no-restore`
Expected: All tests PASS (existing features don't use dotted names, so `EnabledTools` is `null` — no behavior change)

- [ ] **Step 6: Commit**

```bash
git add Infrastructure/Agents/DomainToolRegistry.cs \
  Tests/Unit/Infrastructure/DomainToolRegistryDottedTests.cs
git commit -m "$(cat <<'EOF'
feat: support dotted feature names in DomainToolRegistry

Parse enabledFeatures entries like "filesystem.read" into feature name
+ tool filter. Bare names pass EnabledTools=null (all tools). Dotted
names collect suffixes into a filter set. GetPromptsForFeatures also
resolves dotted names to their parent feature. Backward-compatible:
existing features ignore EnabledTools.
EOF
)"
```

---

## Task 3: VirtualFileSystemRegistry

**Files:**
- Create: `Infrastructure/Agents/VirtualFileSystemRegistry.cs`
- Create: `Tests/Unit/Infrastructure/VirtualFileSystemRegistryTests.cs`

- [ ] **Step 1: Write failing tests**

Create `Tests/Unit/Infrastructure/VirtualFileSystemRegistryTests.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class VirtualFileSystemRegistryTests
{
    private readonly VirtualFileSystemRegistry _registry = new();

    [Fact]
    public async Task DiscoverAsync_RegistersMountsFromFactory()
    {
        var backend = CreateMockBackend("library");
        var factory = CreateMockFactory("http://mcp-text:8080/mcp",
            (new FileSystemMount("library", "/library", "Personal document library"), backend));

        await _registry.DiscoverAsync(["http://mcp-text:8080/mcp"], factory, CancellationToken.None);

        var mounts = _registry.GetMounts();
        mounts.Count.ShouldBe(1);
        mounts[0].Name.ShouldBe("library");
        mounts[0].MountPoint.ShouldBe("/library");
    }

    [Fact]
    public async Task DiscoverAsync_MultipleEndpoints_RegistersAll()
    {
        var libraryBackend = CreateMockBackend("library");
        var vaultBackend = CreateMockBackend("vault");

        var factory = new Mock<IFileSystemBackendFactory>();
        factory.Setup(f => f.DiscoverAsync("http://mcp-text:8080/mcp", It.IsAny<CancellationToken>()))
            .ReturnsAsync([(new FileSystemMount("library", "/library", "Library"), libraryBackend)]);
        factory.Setup(f => f.DiscoverAsync("http://mcp-vault:8080/mcp", It.IsAny<CancellationToken>()))
            .ReturnsAsync([(new FileSystemMount("vault", "/vault", "Vault"), vaultBackend)]);

        await _registry.DiscoverAsync(
            ["http://mcp-text:8080/mcp", "http://mcp-vault:8080/mcp"],
            factory.Object, CancellationToken.None);

        _registry.GetMounts().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Resolve_MatchingMount_ReturnsBackendAndRelativePath()
    {
        var backend = CreateMockBackend("library");
        var factory = CreateMockFactory("http://mcp-text:8080/mcp",
            (new FileSystemMount("library", "/library", "Library"), backend));

        await _registry.DiscoverAsync(["http://mcp-text:8080/mcp"], factory, CancellationToken.None);

        var resolution = _registry.Resolve("/library/notes/todo.md");
        resolution.Backend.ShouldBe(backend);
        resolution.RelativePath.ShouldBe("notes/todo.md");
    }

    [Fact]
    public async Task Resolve_RootPath_ReturnsEmptyRelativePath()
    {
        var backend = CreateMockBackend("library");
        var factory = CreateMockFactory("http://mcp-text:8080/mcp",
            (new FileSystemMount("library", "/library", "Library"), backend));

        await _registry.DiscoverAsync(["http://mcp-text:8080/mcp"], factory, CancellationToken.None);

        var resolution = _registry.Resolve("/library");
        resolution.Backend.ShouldBe(backend);
        resolution.RelativePath.ShouldBe("");
    }

    [Fact]
    public async Task Resolve_LongestPrefixWins()
    {
        var libraryBackend = CreateMockBackend("library");
        var docsBackend = CreateMockBackend("docs");

        var factory = new Mock<IFileSystemBackendFactory>();
        factory.Setup(f => f.DiscoverAsync("http://ep1:8080/mcp", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                (new FileSystemMount("library", "/library", "Library"), libraryBackend),
                (new FileSystemMount("docs", "/library/docs", "Docs"), docsBackend)
            ]);

        await _registry.DiscoverAsync(["http://ep1:8080/mcp"], factory.Object, CancellationToken.None);

        var resolution = _registry.Resolve("/library/docs/readme.md");
        resolution.Backend.ShouldBe(docsBackend);
        resolution.RelativePath.ShouldBe("readme.md");
    }

    [Fact]
    public void Resolve_NoMatchingMount_ThrowsWithAvailableMounts()
    {
        var ex = Should.Throw<InvalidOperationException>(() => _registry.Resolve("/unknown/file.md"));
        ex.Message.ShouldContain("No filesystem mounted");
    }

    [Fact]
    public async Task Resolve_NoMatchingMount_ErrorListsAvailable()
    {
        var backend = CreateMockBackend("library");
        var factory = CreateMockFactory("http://ep:8080/mcp",
            (new FileSystemMount("library", "/library", "Library"), backend));

        await _registry.DiscoverAsync(["http://ep:8080/mcp"], factory, CancellationToken.None);

        var ex = Should.Throw<InvalidOperationException>(() => _registry.Resolve("/unknown/file.md"));
        ex.Message.ShouldContain("/library");
    }

    [Fact]
    public async Task DiscoverAsync_DuplicateMountPoint_LastWriteWins()
    {
        var backend1 = CreateMockBackend("lib1");
        var backend2 = CreateMockBackend("lib2");

        var factory = new Mock<IFileSystemBackendFactory>();
        factory.Setup(f => f.DiscoverAsync("http://ep1:8080/mcp", It.IsAny<CancellationToken>()))
            .ReturnsAsync([(new FileSystemMount("lib1", "/library", "First"), backend1)]);
        factory.Setup(f => f.DiscoverAsync("http://ep2:8080/mcp", It.IsAny<CancellationToken>()))
            .ReturnsAsync([(new FileSystemMount("lib2", "/library", "Second"), backend2)]);

        await _registry.DiscoverAsync(
            ["http://ep1:8080/mcp", "http://ep2:8080/mcp"],
            factory.Object, CancellationToken.None);

        var resolution = _registry.Resolve("/library/file.md");
        resolution.Backend.ShouldBe(backend2);
    }

    [Fact]
    public async Task Resolve_CaseInsensitiveMatch()
    {
        var backend = CreateMockBackend("library");
        var factory = CreateMockFactory("http://ep:8080/mcp",
            (new FileSystemMount("library", "/library", "Library"), backend));

        await _registry.DiscoverAsync(["http://ep:8080/mcp"], factory, CancellationToken.None);

        var resolution = _registry.Resolve("/Library/Notes/Todo.md");
        resolution.Backend.ShouldBe(backend);
        resolution.RelativePath.ShouldBe("Notes/Todo.md");
    }

    private static IFileSystemBackend CreateMockBackend(string name)
    {
        var mock = new Mock<IFileSystemBackend>();
        mock.Setup(b => b.FilesystemName).Returns(name);
        return mock.Object;
    }

    private static IFileSystemBackendFactory CreateMockFactory(
        string endpoint, params (FileSystemMount Mount, IFileSystemBackend Backend)[] results)
    {
        var mock = new Mock<IFileSystemBackendFactory>();
        mock.Setup(f => f.DiscoverAsync(endpoint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(results.ToList());
        return mock.Object;
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VirtualFileSystemRegistryTests" --no-restore`
Expected: FAIL — `VirtualFileSystemRegistry` class doesn't exist

- [ ] **Step 3: Implement `VirtualFileSystemRegistry.cs`**

Create `Infrastructure/Agents/VirtualFileSystemRegistry.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.Agents;

internal sealed class VirtualFileSystemRegistry : IVirtualFileSystemRegistry
{
    private readonly Dictionary<string, (FileSystemMount Mount, IFileSystemBackend Backend)> _mounts = new(StringComparer.OrdinalIgnoreCase);

    public async Task DiscoverAsync(string[] endpoints, IFileSystemBackendFactory backendFactory, CancellationToken ct)
    {
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

Note: The class is `internal` but tests can access it. If the test project doesn't have `InternalsVisibleTo`, change to `public` — check by running the tests.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VirtualFileSystemRegistryTests" --no-restore`
Expected: All 9 tests PASS. If build fails due to `internal` access, make the class `public` and re-run.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/VirtualFileSystemRegistry.cs \
  Tests/Unit/Infrastructure/VirtualFileSystemRegistryTests.cs
git commit -m "$(cat <<'EOF'
feat: implement VirtualFileSystemRegistry with longest-prefix resolution

Registry discovers filesystem mounts from IFileSystemBackendFactory,
stores them by mount point, and resolves virtual paths to backends
using longest-prefix matching. Last-write-wins for duplicate mounts.
Case-insensitive path matching.
EOF
)"
```

---

## Task 4: Domain File Tools (dispatch through registry)

**Files:**
- Create: `Domain/Tools/FileSystem/TextReadTool.cs`
- Create: `Domain/Tools/FileSystem/TextCreateTool.cs`
- Create: `Domain/Tools/FileSystem/TextEditTool.cs`
- Create: `Domain/Tools/FileSystem/GlobFilesTool.cs`
- Create: `Domain/Tools/FileSystem/TextSearchTool.cs`
- Create: `Domain/Tools/FileSystem/MoveTool.cs`
- Create: `Domain/Tools/FileSystem/RemoveTool.cs`
- Create: `Domain/Tools/FileSystem/ListTool.cs`
- Create: `Tests/Unit/Domain/Tools/FileSystem/TextReadToolTests.cs`
- Create: `Tests/Unit/Domain/Tools/FileSystem/TextCreateToolTests.cs`
- Create: `Tests/Unit/Domain/Tools/FileSystem/TextEditToolTests.cs`
- Create: `Tests/Unit/Domain/Tools/FileSystem/GlobFilesToolTests.cs`
- Create: `Tests/Unit/Domain/Tools/FileSystem/TextSearchToolTests.cs`
- Create: `Tests/Unit/Domain/Tools/FileSystem/MoveToolTests.cs`
- Create: `Tests/Unit/Domain/Tools/FileSystem/RemoveToolTests.cs`
- Create: `Tests/Unit/Domain/Tools/FileSystem/ListToolTests.cs`

Each tool follows the same pattern: accept virtual path from LLM, resolve via registry, call backend, return result. Tests mock `IVirtualFileSystemRegistry` and `IFileSystemBackend`.

- [ ] **Step 1: Write failing test for `TextReadTool`**

Create `Tests/Unit/Domain/Tools/FileSystem/TextReadToolTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class TextReadToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly TextReadTool _tool;

    public TextReadToolTests()
    {
        _tool = new TextReadTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndCallsBackend()
    {
        var expected = new JsonObject { ["content"] = "1: hello", ["totalLines"] = 1, ["truncated"] = false };
        _registry.Setup(r => r.Resolve("/library/notes/todo.md"))
            .Returns(new FileSystemResolution(_backend.Object, "notes/todo.md"));
        _backend.Setup(b => b.ReadAsync("notes/todo.md", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/library/notes/todo.md", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_PassesOffsetAndLimit()
    {
        var expected = new JsonObject { ["content"] = "10: line", ["totalLines"] = 100, ["truncated"] = true };
        _registry.Setup(r => r.Resolve("/vault/data.md"))
            .Returns(new FileSystemResolution(_backend.Object, "data.md"));
        _backend.Setup(b => b.ReadAsync("data.md", 10, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/vault/data.md", offset: 10, limit: 50, cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_UnknownMount_ThrowsFromRegistry()
    {
        _registry.Setup(r => r.Resolve("/unknown/file.md"))
            .Throws(new InvalidOperationException("No filesystem mounted"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _tool.RunAsync("/unknown/file.md", cancellationToken: CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FileSystem.TextReadToolTests" --no-restore`
Expected: FAIL — `Domain.Tools.FileSystem.TextReadTool` doesn't exist

- [ ] **Step 3: Implement `TextReadTool.cs`**

Create `Domain/Tools/FileSystem/TextReadTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class TextReadTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "read";
    public const string Name = "text_read";

    public const string ToolDescription = """
        Reads a text file and returns its content with line numbers.
        Returns content formatted as "1: first line\n2: second line\n..." with trailing metadata.
        Large files are truncated — use offset and limit for pagination.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to file (e.g., /library/notes/todo.md)")]
        string filePath,
        [Description("Start from this line number (1-based, default: 1)")]
        int? offset = null,
        [Description("Max lines to return")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(filePath);
        return await resolution.Backend.ReadAsync(resolution.RelativePath, offset, limit, cancellationToken);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FileSystem.TextReadToolTests" --no-restore`
Expected: All 3 tests PASS

- [ ] **Step 5: Write failing tests and implement `TextCreateTool`**

Create `Tests/Unit/Domain/Tools/FileSystem/TextCreateToolTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class TextCreateToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly TextCreateTool _tool;

    public TextCreateToolTests()
    {
        _tool = new TextCreateTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndCallsBackend()
    {
        var expected = new JsonObject { ["status"] = "created", ["path"] = "notes/new.md" };
        _registry.Setup(r => r.Resolve("/library/notes/new.md"))
            .Returns(new FileSystemResolution(_backend.Object, "notes/new.md"));
        _backend.Setup(b => b.CreateAsync("notes/new.md", "# Hello", false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/library/notes/new.md", "# Hello", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_PassesOverwriteAndCreateDirectories()
    {
        var expected = new JsonObject { ["status"] = "created" };
        _registry.Setup(r => r.Resolve("/vault/data.json"))
            .Returns(new FileSystemResolution(_backend.Object, "data.json"));
        _backend.Setup(b => b.CreateAsync("data.json", "{}", true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/vault/data.json", "{}", overwrite: true, createDirectories: false, cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }
}
```

Create `Domain/Tools/FileSystem/TextCreateTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class TextCreateTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "create";
    public const string Name = "text_create";

    public const string ToolDescription = """
        Creates a new text file.
        The file must not already exist unless overwrite is set to true.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path for the new file (e.g., /library/notes/new-topic.md)")]
        string filePath,
        [Description("Initial content for the file")]
        string content,
        [Description("Overwrite if file already exists (default: false)")]
        bool overwrite = false,
        [Description("Create parent directories if they don't exist (default: true)")]
        bool createDirectories = true,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(filePath);
        return await resolution.Backend.CreateAsync(resolution.RelativePath, content, overwrite, createDirectories, cancellationToken);
    }
}
```

- [ ] **Step 6: Write failing tests and implement `TextEditTool`**

Create `Tests/Unit/Domain/Tools/FileSystem/TextEditToolTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class TextEditToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly TextEditTool _tool;

    public TextEditToolTests()
    {
        _tool = new TextEditTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndCallsBackend()
    {
        var expected = new JsonObject { ["status"] = "success", ["occurrencesReplaced"] = 1 };
        _registry.Setup(r => r.Resolve("/library/file.md"))
            .Returns(new FileSystemResolution(_backend.Object, "file.md"));
        _backend.Setup(b => b.EditAsync("file.md", "old", "new", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/library/file.md", "old", "new", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_PassesReplaceAll()
    {
        var expected = new JsonObject { ["status"] = "success", ["occurrencesReplaced"] = 3 };
        _registry.Setup(r => r.Resolve("/vault/config.md"))
            .Returns(new FileSystemResolution(_backend.Object, "config.md"));
        _backend.Setup(b => b.EditAsync("config.md", "foo", "bar", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/vault/config.md", "foo", "bar", replaceAll: true, cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }
}
```

Create `Domain/Tools/FileSystem/TextEditTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class TextEditTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "edit";
    public const string Name = "text_edit";

    public const string ToolDescription = """
        Edits a text file by replacing exact string matches.
        When replaceAll is false, oldString must appear exactly once.
        If multiple occurrences are found, the tool fails — provide more surrounding context in oldString to disambiguate.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to file (e.g., /library/notes/todo.md)")]
        string filePath,
        [Description("Exact text to find (case-sensitive)")]
        string oldString,
        [Description("Replacement text")]
        string newString,
        [Description("Replace all occurrences (default: false)")]
        bool replaceAll = false,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(filePath);
        return await resolution.Backend.EditAsync(resolution.RelativePath, oldString, newString, replaceAll, cancellationToken);
    }
}
```

- [ ] **Step 7: Write failing tests and implement `GlobFilesTool`**

Create `Tests/Unit/Domain/Tools/FileSystem/GlobFilesToolTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class GlobFilesToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly GlobFilesTool _tool;

    public GlobFilesToolTests()
    {
        _tool = new GlobFilesTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesBasePathAndCallsBackend()
    {
        var expected = new JsonObject { ["files"] = new JsonArray("a.md", "b.md") };
        _registry.Setup(r => r.Resolve("/library"))
            .Returns(new FileSystemResolution(_backend.Object, ""));
        _backend.Setup(b => b.GlobAsync("", "**/*.md", "files", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/library", "**/*.md", "files", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_WithSubdirectory_ResolvesRelativePath()
    {
        var expected = new JsonObject { ["directories"] = new JsonObject() };
        _registry.Setup(r => r.Resolve("/vault/docs"))
            .Returns(new FileSystemResolution(_backend.Object, "docs"));
        _backend.Setup(b => b.GlobAsync("docs", "*", "directories", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/vault/docs", "*", "directories", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }
}
```

Create `Domain/Tools/FileSystem/GlobFilesTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class GlobFilesTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "glob";
    public const string Name = "glob_files";

    public const string ToolDescription = """
        Searches for files or directories matching a glob pattern.
        Supports * (single segment), ** (recursive), and ? (single char).
        Use mode 'directories' to explore structure first, then 'files' with specific patterns.
        In files mode, results are capped at 200.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual base path to search from (e.g., /library or /library/docs)")]
        string basePath,
        [Description("Glob pattern (e.g., **/*.md)")]
        string pattern,
        [Description("'files' or 'directories'")]
        string mode = "directories",
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(basePath);
        return await resolution.Backend.GlobAsync(resolution.RelativePath, pattern, mode, cancellationToken);
    }
}
```

- [ ] **Step 8: Write failing tests and implement `TextSearchTool`**

Create `Tests/Unit/Domain/Tools/FileSystem/TextSearchToolTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class TextSearchToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly TextSearchTool _tool;

    public TextSearchToolTests()
    {
        _tool = new TextSearchTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_DirectorySearch_ResolvesAndCallsBackend()
    {
        var expected = new JsonObject { ["totalMatches"] = 5 };
        _registry.Setup(r => r.Resolve("/library"))
            .Returns(new FileSystemResolution(_backend.Object, ""));
        _backend.Setup(b => b.SearchAsync("kubernetes", false, null, "", null, 50, 1, "content", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("kubernetes", directoryPath: "/library", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_SingleFileSearch_ResolvesFilePath()
    {
        var expected = new JsonObject { ["totalMatches"] = 1 };
        _registry.Setup(r => r.Resolve("/vault/notes/todo.md"))
            .Returns(new FileSystemResolution(_backend.Object, "notes/todo.md"));
        _backend.Setup(b => b.SearchAsync("TODO", false, "notes/todo.md", null, null, 50, 1, "content", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("TODO", filePath: "/vault/notes/todo.md", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }
}
```

Create `Domain/Tools/FileSystem/TextSearchTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class TextSearchTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "search";
    public const string Name = "text_search";

    public const string ToolDescription = """
        Searches for text across files in a filesystem, or within a single file.
        Returns matching files with line numbers and context.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Text or regex pattern to search for")]
        string query,
        [Description("Treat query as regex pattern (default: false)")]
        bool regex = false,
        [Description("Search within this single file only (virtual path)")]
        string? filePath = null,
        [Description("Virtual directory path to search in")]
        string? directoryPath = null,
        [Description("Glob pattern to filter files (e.g., *.md)")]
        string? filePattern = null,
        [Description("Maximum number of matches to return (default: 50)")]
        int maxResults = 50,
        [Description("Lines of context around each match (default: 1)")]
        int contextLines = 1,
        [Description("'content' or 'filesOnly' (default: 'content')")]
        string outputMode = "content",
        CancellationToken cancellationToken = default)
    {
        if (filePath is not null)
        {
            var fileResolution = registry.Resolve(filePath);
            return await fileResolution.Backend.SearchAsync(
                query, regex, fileResolution.RelativePath, null, filePattern,
                maxResults, contextLines, outputMode, cancellationToken);
        }

        var dirResolution = registry.Resolve(directoryPath ?? throw new ArgumentException("Either filePath or directoryPath must be provided"));
        return await dirResolution.Backend.SearchAsync(
            query, regex, null, dirResolution.RelativePath, filePattern,
            maxResults, contextLines, outputMode, cancellationToken);
    }
}
```

- [ ] **Step 9: Write failing tests and implement `MoveTool`**

Create `Tests/Unit/Domain/Tools/FileSystem/MoveToolTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class MoveToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly MoveTool _tool;

    public MoveToolTests()
    {
        _tool = new MoveTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_SameFilesystem_ResolvesAndCallsBackend()
    {
        var expected = new JsonObject { ["status"] = "success" };
        _registry.Setup(r => r.Resolve("/library/old/file.md"))
            .Returns(new FileSystemResolution(_backend.Object, "old/file.md"));
        _registry.Setup(r => r.Resolve("/library/new/file.md"))
            .Returns(new FileSystemResolution(_backend.Object, "new/file.md"));
        _backend.Setup(b => b.MoveAsync("old/file.md", "new/file.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/library/old/file.md", "/library/new/file.md", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_DifferentFilesystems_ThrowsClearError()
    {
        var backend2 = new Mock<IFileSystemBackend>().Object;
        _registry.Setup(r => r.Resolve("/library/file.md"))
            .Returns(new FileSystemResolution(_backend.Object, "file.md"));
        _registry.Setup(r => r.Resolve("/vault/file.md"))
            .Returns(new FileSystemResolution(backend2, "file.md"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _tool.RunAsync("/library/file.md", "/vault/file.md", cancellationToken: CancellationToken.None));
    }
}
```

Create `Domain/Tools/FileSystem/MoveTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class MoveTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "move";
    public const string Name = "move";

    public const string ToolDescription = """
        Moves and/or renames a file or directory.
        Both source and destination must be on the same filesystem.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to source file or directory")]
        string sourcePath,
        [Description("Virtual path to destination")]
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var sourceResolution = registry.Resolve(sourcePath);
        var destResolution = registry.Resolve(destinationPath);

        if (sourceResolution.Backend != destResolution.Backend)
        {
            throw new InvalidOperationException(
                "Cannot move between different filesystems. Source and destination must be on the same filesystem.");
        }

        return await sourceResolution.Backend.MoveAsync(
            sourceResolution.RelativePath, destResolution.RelativePath, cancellationToken);
    }
}
```

- [ ] **Step 10: Write failing tests and implement `RemoveTool`**

Create `Tests/Unit/Domain/Tools/FileSystem/RemoveToolTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class RemoveToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly RemoveTool _tool;

    public RemoveToolTests()
    {
        _tool = new RemoveTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndCallsBackend()
    {
        var expected = new JsonObject { ["status"] = "success", ["trashPath"] = ".trash/file.md" };
        _registry.Setup(r => r.Resolve("/library/old/file.md"))
            .Returns(new FileSystemResolution(_backend.Object, "old/file.md"));
        _backend.Setup(b => b.DeleteAsync("old/file.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/library/old/file.md", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }
}
```

Create `Domain/Tools/FileSystem/RemoveTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class RemoveTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "remove";
    public const string Name = "remove";

    public const string ToolDescription = """
        Removes a file or directory by moving it to a trash folder.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to file or directory to remove")]
        string path,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(path);
        return await resolution.Backend.DeleteAsync(resolution.RelativePath, cancellationToken);
    }
}
```

- [ ] **Step 11: Write failing tests and implement `ListTool`**

Create `Tests/Unit/Domain/Tools/FileSystem/ListToolTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class ListToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly ListTool _tool;

    public ListToolTests()
    {
        _tool = new ListTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndCallsBackend()
    {
        var expected = new JsonObject { ["path"] = "docs", ["entries"] = new JsonArray() };
        _registry.Setup(r => r.Resolve("/library/docs"))
            .Returns(new FileSystemResolution(_backend.Object, "docs"));
        _backend.Setup(b => b.ListAsync("docs", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/library/docs", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_RootPath_PassesEmptyRelativePath()
    {
        var expected = new JsonObject { ["path"] = "", ["entries"] = new JsonArray() };
        _registry.Setup(r => r.Resolve("/vault"))
            .Returns(new FileSystemResolution(_backend.Object, ""));
        _backend.Setup(b => b.ListAsync("", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/vault", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }
}
```

Create `Domain/Tools/FileSystem/ListTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class ListTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "list";
    public const string Name = "list_directory";

    public const string ToolDescription = """
        Lists the contents of a directory (non-recursive).
        Returns file names, types, and sizes.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to directory (e.g., /library/docs)")]
        string path,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(path);
        return await resolution.Backend.ListAsync(resolution.RelativePath, cancellationToken);
    }
}
```

- [ ] **Step 12: Run all new tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Domain.Tools.FileSystem" --no-restore`
Expected: All tests PASS

- [ ] **Step 13: Commit**

```bash
git add Domain/Tools/FileSystem/ Tests/Unit/Domain/Tools/FileSystem/
git commit -m "$(cat <<'EOF'
feat: add domain filesystem tools dispatching through virtual registry

Create 8 domain tools (read, create, edit, glob, search, move, remove,
list) that resolve virtual paths via IVirtualFileSystemRegistry and
delegate to IFileSystemBackend. Move tool validates source and
destination are on the same filesystem. Search tool supports both
single-file and directory search modes.
EOF
)"
```

---

## Task 5: FileSystemToolFeature

**Files:**
- Create: `Domain/Tools/FileSystem/FileSystemToolFeature.cs`
- Create: `Tests/Unit/Domain/Tools/FileSystem/FileSystemToolFeatureTests.cs`

- [ ] **Step 1: Write failing tests**

Create `Tests/Unit/Domain/Tools/FileSystem/FileSystemToolFeatureTests.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class FileSystemToolFeatureTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly FileSystemToolFeature _feature;

    public FileSystemToolFeatureTests()
    {
        _registry.Setup(r => r.GetMounts()).Returns([
            new FileSystemMount("library", "/library", "Personal document library")
        ]);
        _feature = new FileSystemToolFeature(_registry.Object);
    }

    [Fact]
    public void FeatureName_IsFilesystem()
    {
        _feature.FeatureName.ShouldBe("filesystem");
    }

    [Fact]
    public void GetTools_NullEnabledTools_ReturnsAllTools()
    {
        var config = new FeatureConfig();
        var tools = _feature.GetTools(config).ToList();

        tools.Count.ShouldBe(8);
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:text_read");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:text_create");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:text_edit");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:glob_files");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:text_search");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:move");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:remove");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:list_directory");
    }

    [Fact]
    public void GetTools_FilteredEnabledTools_ReturnsOnlyMatching()
    {
        var config = new FeatureConfig(
            EnabledTools: new HashSet<string>(["read", "move"], StringComparer.OrdinalIgnoreCase));
        var tools = _feature.GetTools(config).ToList();

        tools.Count.ShouldBe(2);
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:text_read");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:move");
    }

    [Fact]
    public void GetTools_EmptyEnabledTools_ReturnsNoTools()
    {
        var config = new FeatureConfig(
            EnabledTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var tools = _feature.GetTools(config).ToList();

        tools.ShouldBeEmpty();
    }

    [Fact]
    public void Prompt_ContainsMountPoints()
    {
        _feature.Prompt.ShouldNotBeNull();
        _feature.Prompt.ShouldContain("/library");
        _feature.Prompt.ShouldContain("Personal document library");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FileSystemToolFeatureTests" --no-restore`
Expected: FAIL — `FileSystemToolFeature` doesn't exist

- [ ] **Step 3: Implement `FileSystemToolFeature.cs`**

Create `Domain/Tools/FileSystem/FileSystemToolFeature.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

public class FileSystemToolFeature(IVirtualFileSystemRegistry registry) : IDomainToolFeature
{
    private const string Feature = "filesystem";

    public string FeatureName => Feature;

    public string? Prompt => BuildPrompt();

    public IEnumerable<AIFunction> GetTools(FeatureConfig config)
    {
        var tools = new (string Key, Func<AIFunction> Factory)[]
        {
            (TextReadTool.Key, () => CreateFunction(new TextReadTool(registry))),
            (TextCreateTool.Key, () => CreateFunction(new TextCreateTool(registry))),
            (TextEditTool.Key, () => CreateFunction(new TextEditTool(registry))),
            (GlobFilesTool.Key, () => CreateFunction(new GlobFilesTool(registry))),
            (TextSearchTool.Key, () => CreateFunction(new TextSearchTool(registry))),
            (MoveTool.Key, () => CreateFunction(new MoveTool(registry))),
            (RemoveTool.Key, () => CreateFunction(new RemoveTool(registry))),
            (ListTool.Key, () => CreateFunction(new ListTool(registry))),
        };

        return tools
            .Where(t => config.EnabledTools is null || config.EnabledTools.Contains(t.Key))
            .Select(t => t.Factory());
    }

    private AIFunction CreateFunction<T>(T tool) where T : class
    {
        var name = tool switch
        {
            TextReadTool => TextReadTool.Name,
            TextCreateTool => TextCreateTool.Name,
            TextEditTool => TextEditTool.Name,
            GlobFilesTool => GlobFilesTool.Name,
            TextSearchTool => TextSearchTool.Name,
            MoveTool => MoveTool.Name,
            RemoveTool => RemoveTool.Name,
            ListTool => ListTool.Name,
            _ => throw new ArgumentException($"Unknown tool type: {tool.GetType().Name}")
        };

        var method = tool.GetType().GetMethod("RunAsync")
            ?? throw new InvalidOperationException($"Tool {tool.GetType().Name} missing RunAsync method");

        return AIFunctionFactory.Create(method, tool, name: $"domain:{Feature}:{name}");
    }

    private string? BuildPrompt()
    {
        var mounts = registry.GetMounts();
        if (mounts.Count == 0) return null;

        var mountList = string.Join("\n", mounts.Select(m => $"- {m.MountPoint} — {m.Description}"));
        return $"## Available Filesystems\n\nAll file tool paths must start with one of these prefixes:\n{mountList}";
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FileSystemToolFeatureTests" --no-restore`
Expected: All 5 tests PASS

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/FileSystem/FileSystemToolFeature.cs \
  Tests/Unit/Domain/Tools/FileSystem/FileSystemToolFeatureTests.cs
git commit -m "$(cat <<'EOF'
feat: add FileSystemToolFeature with per-tool filtering

IDomainToolFeature implementation that registers filesystem tools
filtered by FeatureConfig.EnabledTools. Generates a system prompt
listing available filesystem mount points.
EOF
)"
```

---

## Task 6: McpFileSystemBackend and BackendFactory

**Files:**
- Create: `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`
- Create: `Infrastructure/Agents/Mcp/McpFileSystemBackendFactory.cs`

These classes wrap MCP client calls to `fs_*` tools and discover `filesystem://` resources. They are infrastructure-level and will be integration-tested when we wire everything together. Unit tests would require mocking `McpClient` internals which adds little value — the real validation is end-to-end.

- [ ] **Step 1: Implement `McpFileSystemBackend.cs`**

Create `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using ModelContextProtocol.Client;

namespace Infrastructure.Agents.Mcp;

internal sealed class McpFileSystemBackend(McpClient client, string filesystemName) : IFileSystemBackend
{
    public string FilesystemName => filesystemName;

    public async Task<JsonNode> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        return await CallToolAsync("fs_read", new Dictionary<string, object?>
        {
            ["filesystem"] = filesystemName,
            ["path"] = path,
            ["offset"] = offset,
            ["limit"] = limit
        }, ct);
    }

    public async Task<JsonNode> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        return await CallToolAsync("fs_create", new Dictionary<string, object?>
        {
            ["filesystem"] = filesystemName,
            ["path"] = path,
            ["content"] = content,
            ["overwrite"] = overwrite,
            ["createDirectories"] = createDirectories
        }, ct);
    }

    public async Task<JsonNode> EditAsync(string path, string oldString, string newString, bool replaceAll, CancellationToken ct)
    {
        return await CallToolAsync("fs_edit", new Dictionary<string, object?>
        {
            ["filesystem"] = filesystemName,
            ["path"] = path,
            ["oldString"] = oldString,
            ["newString"] = newString,
            ["replaceAll"] = replaceAll
        }, ct);
    }

    public async Task<JsonNode> GlobAsync(string basePath, string pattern, string mode, CancellationToken ct)
    {
        return await CallToolAsync("fs_glob", new Dictionary<string, object?>
        {
            ["filesystem"] = filesystemName,
            ["basePath"] = basePath,
            ["pattern"] = pattern,
            ["mode"] = mode
        }, ct);
    }

    public async Task<JsonNode> SearchAsync(string query, bool regex, string? path, string? directoryPath,
        string? filePattern, int maxResults, int contextLines, string outputMode, CancellationToken ct)
    {
        return await CallToolAsync("fs_search", new Dictionary<string, object?>
        {
            ["filesystem"] = filesystemName,
            ["query"] = query,
            ["regex"] = regex,
            ["path"] = path,
            ["directoryPath"] = directoryPath,
            ["filePattern"] = filePattern,
            ["maxResults"] = maxResults,
            ["contextLines"] = contextLines,
            ["outputMode"] = outputMode
        }, ct);
    }

    public async Task<JsonNode> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        return await CallToolAsync("fs_move", new Dictionary<string, object?>
        {
            ["filesystem"] = filesystemName,
            ["sourcePath"] = sourcePath,
            ["destinationPath"] = destinationPath
        }, ct);
    }

    public async Task<JsonNode> DeleteAsync(string path, CancellationToken ct)
    {
        return await CallToolAsync("fs_delete", new Dictionary<string, object?>
        {
            ["filesystem"] = filesystemName,
            ["path"] = path
        }, ct);
    }

    public async Task<JsonNode> ListAsync(string path, CancellationToken ct)
    {
        return await CallToolAsync("fs_list", new Dictionary<string, object?>
        {
            ["filesystem"] = filesystemName,
            ["path"] = path
        }, ct);
    }

    private async Task<JsonNode> CallToolAsync(string toolName, Dictionary<string, object?> args, CancellationToken ct)
    {
        var result = await client.CallToolAsync(toolName, args, cancellationToken: ct);

        if (result.IsError)
        {
            var errorText = string.Join("\n", result.Content
                .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                .Select(c => c.Text));
            throw new InvalidOperationException(errorText);
        }

        var text = string.Join("\n", result.Content
            .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
            .Select(c => c.Text));

        return JsonNode.Parse(text)
            ?? throw new InvalidOperationException($"Failed to parse response from {toolName}");
    }
}
```

- [ ] **Step 2: Implement `McpFileSystemBackendFactory.cs`**

Create `Infrastructure/Agents/Mcp/McpFileSystemBackendFactory.cs`:

```csharp
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Polly;

namespace Infrastructure.Agents.Mcp;

internal sealed class McpFileSystemBackendFactory(ILogger<McpFileSystemBackendFactory> logger) : IFileSystemBackendFactory
{
    private const string ResourcePrefix = "filesystem://";

    public async Task<IReadOnlyList<(FileSystemMount Mount, IFileSystemBackend Backend)>> DiscoverAsync(
        string endpoint, CancellationToken ct)
    {
        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

        var client = await retryPolicy.ExecuteAsync(() => McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(endpoint) }),
            new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = "filesystem-discovery",
                    Description = "Filesystem backend discovery",
                    Version = "1.0.0"
                }
            },
            cancellationToken: ct));

        var resources = await client.ListResourcesAsync(cancellationToken: ct);
        var filesystemResources = resources
            .Where(r => r.Uri.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filesystemResources.Count == 0)
        {
            logger.LogWarning("No filesystem resources found at endpoint {Endpoint}", endpoint);
            await client.DisposeAsync();
            return [];
        }

        var results = new List<(FileSystemMount, IFileSystemBackend)>();

        foreach (var resource in filesystemResources)
        {
            var content = await client.ReadResourceAsync(resource.Uri, cancellationToken: ct);
            var text = string.Join("", content.Contents
                .OfType<TextResourceContents>()
                .Select(c => c.Text));

            var metadata = JsonSerializer.Deserialize<FileSystemResourceMetadata>(text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (metadata is null || string.IsNullOrEmpty(metadata.Name) || string.IsNullOrEmpty(metadata.MountPoint))
            {
                logger.LogWarning("Invalid filesystem resource metadata at {Uri}", resource.Uri);
                continue;
            }

            var mount = new FileSystemMount(metadata.Name, metadata.MountPoint, metadata.Description ?? "");
            var backend = new McpFileSystemBackend(client, metadata.Name);
            results.Add((mount, backend));

            logger.LogInformation("Discovered filesystem '{Name}' at mount point '{MountPoint}' from {Endpoint}",
                metadata.Name, metadata.MountPoint, endpoint);
        }

        return results;
    }

    private record FileSystemResourceMetadata(string Name, string MountPoint, string? Description);
}
```

- [ ] **Step 3: Verify the project builds**

Run: `dotnet build Infrastructure/Infrastructure.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add Infrastructure/Agents/Mcp/McpFileSystemBackend.cs \
  Infrastructure/Agents/Mcp/McpFileSystemBackendFactory.cs
git commit -m "$(cat <<'EOF'
feat: add McpFileSystemBackend and BackendFactory

McpFileSystemBackend wraps MCP client calls to fs_* tools, mapping
IFileSystemBackend methods to standardized tool invocations.
McpFileSystemBackendFactory connects to MCP endpoints, discovers
filesystem:// resources, and creates backends per filesystem.
EOF
)"
```

---

## Task 7: McpServerText Transformation

**Files:**
- Create: `McpServerText/McpResources/FileSystemResource.cs`
- Create: `McpServerText/McpTools/FsReadTool.cs`
- Create: `McpServerText/McpTools/FsCreateTool.cs`
- Create: `McpServerText/McpTools/FsEditTool.cs`
- Create: `McpServerText/McpTools/FsGlobTool.cs`
- Create: `McpServerText/McpTools/FsSearchTool.cs`
- Create: `McpServerText/McpTools/FsMoveTool.cs`
- Create: `McpServerText/McpTools/FsDeleteTool.cs`
- Create: `McpServerText/McpTools/FsListTool.cs`
- Delete: `McpServerText/McpTools/McpTextReadTool.cs`
- Delete: `McpServerText/McpTools/McpTextEditTool.cs`
- Delete: `McpServerText/McpTools/McpTextCreateTool.cs`
- Delete: `McpServerText/McpTools/McpTextSearchTool.cs`
- Delete: `McpServerText/McpTools/McpTextGlobFilesTool.cs`
- Delete: `McpServerText/McpTools/McpMoveTool.cs`
- Delete: `McpServerText/McpTools/McpRemoveTool.cs`
- Modify: `McpServerText/Modules/ConfigModule.cs`

- [ ] **Step 1: Create `FileSystemResource.cs`**

Create `McpServerText/McpResources/FileSystemResource.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using McpServerText.Settings;
using ModelContextProtocol.Server;

namespace McpServerText.McpResources;

[McpServerResourceType]
public class FileSystemResource(McpSettings settings)
{
    [McpServerResource(
        UriTemplate = "filesystem://library",
        Name = "Library Filesystem",
        MimeType = "application/json")]
    [Description("Personal document library filesystem")]
    public string GetLibraryInfo()
    {
        return JsonSerializer.Serialize(new
        {
            name = "library",
            mountPoint = "/library",
            description = $"Personal document library ({settings.VaultPath})"
        });
    }
}
```

- [ ] **Step 2: Create `FsReadTool.cs`**

The `Fs*Tool` classes reuse the existing domain tool logic from `Domain/Tools/Text/` and `Domain/Tools/Files/` internally. They inherit from the old domain tools and wrap them with the standardized `fs_*` MCP tool interface. The `filesystem` parameter is accepted but currently only `"library"` is valid.

Create `McpServerText/McpTools/FsReadTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerText.Settings;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class FsReadTool(McpSettings settings)
    : TextReadTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = "fs_read")]
    [Description("Read file content with optional pagination")]
    public CallToolResult McpRun(
        string filesystem,
        string path,
        int? offset = null,
        int? limit = null)
    {
        return ToolResponse.Create(Run(path, offset, limit));
    }
}
```

- [ ] **Step 3: Create `FsCreateTool.cs`**

Create `McpServerText/McpTools/FsCreateTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerText.Settings;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class FsCreateTool(McpSettings settings)
    : TextCreateTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = "fs_create")]
    [Description("Create a new file")]
    public CallToolResult McpRun(
        string filesystem,
        string path,
        string content,
        bool overwrite = false,
        bool createDirectories = true)
    {
        return ToolResponse.Create(Run(path, content, overwrite, createDirectories));
    }
}
```

- [ ] **Step 4: Create `FsEditTool.cs`**

Create `McpServerText/McpTools/FsEditTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerText.Settings;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class FsEditTool(McpSettings settings)
    : TextEditTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = "fs_edit")]
    [Description("Edit file via exact string replacement")]
    public CallToolResult McpRun(
        string filesystem,
        string path,
        string oldString,
        string newString,
        bool replaceAll = false)
    {
        return ToolResponse.Create(Run(path, oldString, newString, replaceAll));
    }
}
```

- [ ] **Step 5: Create `FsGlobTool.cs`**

Create `McpServerText/McpTools/FsGlobTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class FsGlobTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : GlobFilesTool(client, libraryPath)
{
    [McpServerTool(Name = "fs_glob")]
    [Description("Search for files or directories matching a glob pattern")]
    public async Task<CallToolResult> McpRun(
        string filesystem,
        string pattern,
        string mode = "directories",
        string basePath = "",
        CancellationToken cancellationToken = default)
    {
        var globMode = mode.Equals("files", StringComparison.OrdinalIgnoreCase)
            ? GlobMode.Files
            : GlobMode.Directories;

        var effectivePattern = string.IsNullOrEmpty(basePath)
            ? pattern
            : $"{basePath.TrimEnd('/')}/{pattern}";

        return ToolResponse.Create(await Run(effectivePattern, globMode, cancellationToken));
    }
}
```

- [ ] **Step 6: Create `FsSearchTool.cs`**

Create `McpServerText/McpTools/FsSearchTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerText.Settings;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class FsSearchTool(McpSettings settings)
    : TextSearchTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = "fs_search")]
    [Description("Search file contents with text or regex")]
    public CallToolResult McpRun(
        string filesystem,
        string query,
        bool regex = false,
        string? path = null,
        string? directoryPath = null,
        string? filePattern = null,
        int maxResults = 50,
        int contextLines = 1,
        string outputMode = "content")
    {
        var searchOutputMode = outputMode.Equals("filesOnly", StringComparison.OrdinalIgnoreCase)
            ? SearchOutputMode.FilesOnly
            : SearchOutputMode.Content;

        var effectiveDirectoryPath = directoryPath ?? "/";

        return ToolResponse.Create(Run(query, regex, path, filePattern, effectiveDirectoryPath, maxResults, contextLines, searchOutputMode));
    }
}
```

- [ ] **Step 7: Create `FsMoveTool.cs`**

Create `McpServerText/McpTools/FsMoveTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class FsMoveTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : MoveTool(client, libraryPath)
{
    [McpServerTool(Name = "fs_move")]
    [Description("Move or rename a file or directory")]
    public async Task<CallToolResult> McpRun(
        string filesystem,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(await Run(sourcePath, destinationPath, cancellationToken));
    }
}
```

- [ ] **Step 8: Create `FsDeleteTool.cs`**

Create `McpServerText/McpTools/FsDeleteTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class FsDeleteTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : RemoveTool(client, libraryPath)
{
    [McpServerTool(Name = "fs_delete")]
    [Description("Delete a file or directory (move to trash)")]
    public async Task<CallToolResult> McpRun(
        string filesystem,
        string path,
        CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(await Run(path, cancellationToken));
    }
}
```

- [ ] **Step 9: Create `FsListTool.cs`**

This is a new tool — no existing domain tool to inherit from. It uses `IFileSystemClient.DescribeDirectory` and direct filesystem calls.

Create `McpServerText/McpTools/FsListTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Tools.Config;
using Infrastructure.Utils;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class FsListTool(LibraryPathConfig libraryPath)
{
    [McpServerTool(Name = "fs_list")]
    [Description("List directory contents (non-recursive)")]
    public CallToolResult McpRun(
        string filesystem,
        string path = "")
    {
        var fullPath = string.IsNullOrEmpty(path)
            ? libraryPath.BaseLibraryPath
            : Path.GetFullPath(Path.Combine(libraryPath.BaseLibraryPath, path));

        if (!fullPath.StartsWith(libraryPath.BaseLibraryPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Access denied: path must be within library directory");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {path}");
        }

        var entries = new JsonArray();

        foreach (var dir in Directory.GetDirectories(fullPath))
        {
            entries.Add(new JsonObject
            {
                ["name"] = Path.GetFileName(dir),
                ["type"] = "directory"
            });
        }

        foreach (var file in Directory.GetFiles(fullPath))
        {
            var info = new FileInfo(file);
            entries.Add(new JsonObject
            {
                ["name"] = info.Name,
                ["type"] = "file",
                ["size"] = FormatSize(info.Length)
            });
        }

        return ToolResponse.Create(new JsonObject
        {
            ["path"] = path,
            ["entries"] = entries
        });
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1}MB"
    };
}
```

- [ ] **Step 10: Delete old MCP tool wrappers**

```bash
rm McpServerText/McpTools/McpTextReadTool.cs \
   McpServerText/McpTools/McpTextEditTool.cs \
   McpServerText/McpTools/McpTextCreateTool.cs \
   McpServerText/McpTools/McpTextSearchTool.cs \
   McpServerText/McpTools/McpTextGlobFilesTool.cs \
   McpServerText/McpTools/McpMoveTool.cs \
   McpServerText/McpTools/McpRemoveTool.cs
```

- [ ] **Step 11: Update `McpServerText/Modules/ConfigModule.cs`**

Replace the tool and resource registrations in the `ConfigureMcp` method. Replace the `.WithTools<...>()` chain with:

```csharp
            // Filesystem backend tools
            .WithTools<FsReadTool>()
            .WithTools<FsCreateTool>()
            .WithTools<FsEditTool>()
            .WithTools<FsGlobTool>()
            .WithTools<FsSearchTool>()
            .WithTools<FsMoveTool>()
            .WithTools<FsDeleteTool>()
            .WithTools<FsListTool>()
            // Filesystem resource
            .WithResources<FileSystemResource>()
            // Prompts
            .WithPrompts<McpSystemPrompt>();
```

Make sure to add `using McpServerText.McpResources;` to the imports.

- [ ] **Step 12: Verify the project builds**

Run: `dotnet build McpServerText/McpServerText.csproj`
Expected: Build succeeded

- [ ] **Step 13: Commit**

```bash
git add McpServerText/McpResources/FileSystemResource.cs \
  McpServerText/McpTools/FsReadTool.cs McpServerText/McpTools/FsCreateTool.cs \
  McpServerText/McpTools/FsEditTool.cs McpServerText/McpTools/FsGlobTool.cs \
  McpServerText/McpTools/FsSearchTool.cs McpServerText/McpTools/FsMoveTool.cs \
  McpServerText/McpTools/FsDeleteTool.cs McpServerText/McpTools/FsListTool.cs \
  McpServerText/Modules/ConfigModule.cs
git add -u McpServerText/McpTools/  # stages deletions
git commit -m "$(cat <<'EOF'
feat: transform McpServerText into filesystem backend MCP

Replace Mcp*Tool wrappers with Fs*Tool classes implementing the
standardized fs_* protocol. Add filesystem://library resource for
discovery. Add FsListTool (new operation). Remove old tool wrappers.
EOF
)"
```

---

## Task 8: ThreadSessionBuilder and McpAgent Integration

**Files:**
- Modify: `Infrastructure/Agents/ThreadSession.cs`
- Modify: `Infrastructure/Agents/McpAgent.cs`
- Modify: `Infrastructure/Agents/MultiAgentFactory.cs`

- [ ] **Step 1: Modify `ThreadSession.cs` — add filesystem discovery**

The `ThreadSessionBuilder` needs to:
1. Accept `fileSystemEndpoints` separately from regular `endpoints`
2. Create a `VirtualFileSystemRegistry` and discover filesystem backends
3. The registry is then available for domain tools (passed via constructor)

Update `ThreadSession.CreateAsync` signature to accept `fileSystemEndpoints` and an `IFileSystemBackendFactory`:

In `ThreadSession.cs`, modify `CreateAsync`:

```csharp
public static async Task<ThreadSession> CreateAsync(
    string[] endpoints,
    string[] fileSystemEndpoints,
    string name,
    string userId,
    string description,
    ChatClientAgent agent,
    AgentSession thread,
    IReadOnlyList<AIFunction> domainTools,
    IFileSystemBackendFactory? fileSystemBackendFactory,
    CancellationToken ct,
    bool enableResourceSubscriptions = true)
{
    var builder = new ThreadSessionBuilder(endpoints, fileSystemEndpoints, name, description,
        agent, thread, userId, domainTools, fileSystemBackendFactory);
    var data = await builder.BuildAsync(ct, enableResourceSubscriptions);
    return new ThreadSession(data);
}
```

Update `ThreadSessionData` to include the registry:

```csharp
internal sealed record ThreadSessionData(
    McpClientManager ClientManager,
    McpResourceManager? ResourceManager,
    IReadOnlyList<AITool> Tools,
    IVirtualFileSystemRegistry? FileSystemRegistry);
```

Update `ThreadSessionBuilder` constructor and `BuildAsync`:

```csharp
internal sealed class ThreadSessionBuilder(
    string[] endpoints,
    string[] fileSystemEndpoints,
    string name,
    string description,
    ChatClientAgent agent,
    AgentSession thread,
    string userId,
    IReadOnlyList<AIFunction> domainTools,
    IFileSystemBackendFactory? fileSystemBackendFactory)
{
    private IReadOnlyList<AITool> _tools = [];

    public async Task<ThreadSessionData> BuildAsync(CancellationToken ct, bool enableResourceSubscriptions = true)
    {
        // Step 1: Create sampling handler with deferred tool access
        var samplingHandler = new McpSamplingHandler(agent, () => _tools);
        var handlers = new McpClientHandlers { SamplingHandler = samplingHandler.HandleAsync };

        // Step 2: Create MCP clients for tool servers and load tools/prompts
        var clientManager = await McpClientManager.CreateAsync(name, userId, description, endpoints, handlers, ct);

        // Step 3: Discover filesystem backends (if any endpoints configured)
        IVirtualFileSystemRegistry? registry = null;
        if (fileSystemEndpoints.Length > 0 && fileSystemBackendFactory is not null)
        {
            registry = new VirtualFileSystemRegistry();
            await registry.DiscoverAsync(fileSystemEndpoints, fileSystemBackendFactory, ct);
        }

        // Step 4: Combine MCP tools with domain tools
        _tools = clientManager.Tools.Concat(domainTools).ToList();

        // Step 5: Setup resource management (skipped for subagents)
        McpResourceManager? resourceManager = enableResourceSubscriptions
            ? await CreateResourceManagerAsync(clientManager, ct)
            : null;

        return new ThreadSessionData(clientManager, resourceManager, _tools, registry);
    }

    private async Task<McpResourceManager> CreateResourceManagerAsync(
        McpClientManager clientManager,
        CancellationToken ct)
    {
        var instructions = string.Join("\n\n", clientManager.Prompts);
        var resourceManager = new McpResourceManager(agent, thread, instructions, _tools);

        await resourceManager.SyncResourcesAsync(clientManager.Clients, ct);
        resourceManager.SubscribeToNotifications(clientManager.Clients);

        return resourceManager;
    }
}
```

Add necessary imports at the top of the file:

```csharp
using Domain.Contracts;
```

- [ ] **Step 2: Modify `McpAgent.cs` — accept filesystem endpoints and factory**

Add new fields and update the constructor. Add `fileSystemEndpoints` and `fileSystemBackendFactory` parameters:

In the constructor (after `_endpoints = endpoints;`), add:
```csharp
_fileSystemEndpoints = fileSystemEndpoints;
_fileSystemBackendFactory = fileSystemBackendFactory;
```

Add fields:
```csharp
private readonly string[] _fileSystemEndpoints;
private readonly IFileSystemBackendFactory? _fileSystemBackendFactory;
```

Update the `McpAgent` constructor signature:
```csharp
public McpAgent(
    string[] endpoints,
    string[] fileSystemEndpoints,
    IChatClient chatClient,
    string name,
    string description,
    IThreadStateStore stateStore,
    string userId,
    string? customInstructions = null,
    IReadOnlyList<AIFunction>? domainTools = null,
    IReadOnlyList<string>? domainPrompts = null,
    bool enableResourceSubscriptions = true,
    IFileSystemBackendFactory? fileSystemBackendFactory = null)
```

Update `GetOrCreateSessionAsync` to pass the new params:
```csharp
var newSession = await ThreadSession
    .CreateAsync(_endpoints, _fileSystemEndpoints, _name, _userId, _description, _innerAgent,
                 thread, _domainTools, _fileSystemBackendFactory, ct, _enableResourceSubscriptions);
```

Add import:
```csharp
using Domain.Contracts;
```

- [ ] **Step 3: Modify `MultiAgentFactory.cs` — pass filesystem endpoints**

Inject `IFileSystemBackendFactory` into `MultiAgentFactory`:

```csharp
public sealed class MultiAgentFactory(
    IServiceProvider serviceProvider,
    IAgentDefinitionProvider definitionProvider,
    OpenRouterConfig openRouterConfig,
    IDomainToolRegistry domainToolRegistry,
    IFileSystemBackendFactory? fileSystemBackendFactory = null,
    IMetricsPublisher? metricsPublisher = null) : IAgentFactory, IScheduleAgentFactory
```

In `CreateFromDefinition`, update the `McpAgent` construction to pass `definition.FileSystemEndpoints` and `fileSystemBackendFactory`:

```csharp
return new McpAgent(
    definition.McpServerEndpoints,
    definition.FileSystemEndpoints,
    effectiveClient,
    name,
    definition.Description ?? "",
    stateStore,
    userId,
    definition.CustomInstructions,
    domainTools,
    domainPrompts,
    fileSystemBackendFactory: fileSystemBackendFactory);
```

Similarly update `CreateSubAgent`:

```csharp
return new McpAgent(
    definition.McpServerEndpoints,
    definition.FileSystemEndpoints,
    effectiveClient,
    $"subagent-{definition.Id}",
    definition.Description ?? "",
    new NullThreadStateStore(),
    userId,
    definition.CustomInstructions,
    domainTools,
    domainPrompts,
    enableResourceSubscriptions: false,
    fileSystemBackendFactory: fileSystemBackendFactory);
```

Add import:
```csharp
using Domain.Contracts;
```

- [ ] **Step 4: Verify the project builds**

Run: `dotnet build Agent/Agent.csproj`
Expected: Build succeeded (or compile errors to fix — filesystem feature not registered in DI yet, but the factory is optional/nullable so it should build)

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/ThreadSession.cs Infrastructure/Agents/McpAgent.cs \
  Infrastructure/Agents/MultiAgentFactory.cs
git commit -m "$(cat <<'EOF'
feat: integrate filesystem discovery into ThreadSession and McpAgent

ThreadSessionBuilder discovers filesystem backends from separate
fileSystemEndpoints, creates VirtualFileSystemRegistry. McpAgent
accepts fileSystemEndpoints and IFileSystemBackendFactory, passes
them through to session creation. MultiAgentFactory injects the
backend factory.
EOF
)"
```

---

## Task 9: DI Registration and Configuration

**Files:**
- Create: `Agent/Modules/FileSystemModule.cs`
- Modify: `Agent/Modules/ConfigModule.cs`
- Modify: `Agent/appsettings.json`
- Modify: `DockerCompose/docker-compose.yml` (if needed)

- [ ] **Step 1: Create `FileSystemModule.cs`**

Create `Agent/Modules/FileSystemModule.cs`:

```csharp
using Domain.Contracts;
using Domain.Tools.FileSystem;
using Infrastructure.Agents.Mcp;

namespace Agent.Modules;

public static class FileSystemModule
{
    public static IServiceCollection AddFileSystem(this IServiceCollection services)
    {
        services.AddSingleton<IFileSystemBackendFactory, McpFileSystemBackendFactory>();
        services.AddTransient<IDomainToolFeature, FileSystemToolFeature>();
        return services;
    }
}
```

Note: `FileSystemToolFeature` takes `IVirtualFileSystemRegistry` in its constructor. Since the registry is created per-session (inside `ThreadSessionBuilder`), not via DI, the `FileSystemToolFeature` registered here won't have a registry at DI resolve time. 

This means the `FileSystemToolFeature` needs to work differently from other features — the registry is session-scoped, not singleton. The domain tools are created inside `FileSystemToolFeature.GetTools()` which is called from `MultiAgentFactory` before a session exists.

**Revised approach:** The registry must be injected later, after session creation. The cleanest way: `FileSystemToolFeature` doesn't go through DI as `IDomainToolFeature`. Instead, `MultiAgentFactory` creates it directly after the session is built, or the domain tools are created with a deferred registry.

Actually, looking at the architecture more carefully: `domainTools` are created in `MultiAgentFactory` and passed to `McpAgent`, which passes them to `ThreadSession`. But filesystem tools need the registry, which is created inside `ThreadSession`. This is a circular dependency.

**Resolution:** The filesystem tools need to be created after discovery, inside `ThreadSessionBuilder.BuildAsync()`. The `McpAgent` already has a pattern for this — `_domainTools` are combined with MCP tools in step 3 of the builder. We can create filesystem domain tools there too.

Revise the approach: Instead of registering `FileSystemToolFeature` as `IDomainToolFeature` in DI, have the `ThreadSessionBuilder` create filesystem tools after discovery and add them to the combined tool list.

Update `FileSystemModule.cs`:

```csharp
using Domain.Contracts;
using Infrastructure.Agents.Mcp;

namespace Agent.Modules;

public static class FileSystemModule
{
    public static IServiceCollection AddFileSystem(this IServiceCollection services)
    {
        services.AddSingleton<IFileSystemBackendFactory, McpFileSystemBackendFactory>();
        return services;
    }
}
```

- [ ] **Step 2: Update `ThreadSessionBuilder.BuildAsync` to create filesystem tools**

In `Infrastructure/Agents/ThreadSession.cs`, after the registry is discovered (step 3), create the filesystem domain tools:

```csharp
// Step 3: Discover filesystem backends (if any endpoints configured)
IVirtualFileSystemRegistry? registry = null;
IReadOnlyList<AIFunction> fileSystemTools = [];
if (fileSystemEndpoints.Length > 0 && fileSystemBackendFactory is not null)
{
    registry = new VirtualFileSystemRegistry();
    await registry.DiscoverAsync(fileSystemEndpoints, fileSystemBackendFactory, ct);

    var feature = new FileSystemToolFeature(registry);
    fileSystemTools = feature.GetTools(featureConfig).ToList();
}

// Step 4: Combine MCP tools with domain tools and filesystem tools
_tools = clientManager.Tools.Concat(domainTools).Concat(fileSystemTools).ToList();
```

The `FileSystemToolFeature.GetTools` needs the filesystem-specific `FeatureConfig` (with `EnabledTools` for the filesystem feature). `DomainToolRegistry` already parses dotted names and creates per-feature configs. The `ThreadSessionBuilder` needs the filesystem-specific `EnabledTools` extracted from the agent's `enabledFeatures`.

Update the constructor to accept `filesystemEnabledTools`:

```csharp
internal sealed class ThreadSessionBuilder(
    string[] endpoints,
    string[] fileSystemEndpoints,
    string name,
    string description,
    ChatClientAgent agent,
    AgentSession thread,
    string userId,
    IReadOnlyList<AIFunction> domainTools,
    IFileSystemBackendFactory? fileSystemBackendFactory,
    IReadOnlySet<string>? filesystemEnabledTools)
```

In `BuildAsync`, use it when creating the feature:
```csharp
var fsFeatureConfig = new FeatureConfig(EnabledTools: filesystemEnabledTools);
var feature = new FileSystemToolFeature(registry);
fileSystemTools = feature.GetTools(fsFeatureConfig).ToList();
```

And `ThreadSession.CreateAsync`:

```csharp
public static async Task<ThreadSession> CreateAsync(
    string[] endpoints,
    string[] fileSystemEndpoints,
    string name,
    string userId,
    string description,
    ChatClientAgent agent,
    AgentSession thread,
    IReadOnlyList<AIFunction> domainTools,
    IFileSystemBackendFactory? fileSystemBackendFactory,
    IReadOnlySet<string>? filesystemEnabledTools,
    CancellationToken ct,
    bool enableResourceSubscriptions = true)
{
    var builder = new ThreadSessionBuilder(endpoints, fileSystemEndpoints, name, description,
        agent, thread, userId, domainTools, fileSystemBackendFactory, filesystemEnabledTools);
    var data = await builder.BuildAsync(ct, enableResourceSubscriptions);
    return new ThreadSession(data);
}
```

Add the necessary imports:

```csharp
using Domain.DTOs;
using Domain.Tools.FileSystem;
```

- [ ] **Step 3: Update `McpAgent` to extract and pass filesystem `EnabledTools`**

`McpAgent` needs to parse `enabledFeatures` to extract the filesystem tool filter. Add a helper and new fields:

Add field:
```csharp
private readonly IReadOnlySet<string>? _filesystemEnabledTools;
```

Add parameter to constructor:
```csharp
IReadOnlySet<string>? filesystemEnabledTools = null
```

In constructor body:
```csharp
_filesystemEnabledTools = filesystemEnabledTools;
```

Update `GetOrCreateSessionAsync`:
```csharp
var newSession = await ThreadSession
    .CreateAsync(_endpoints, _fileSystemEndpoints, _name, _userId, _description, _innerAgent,
                 thread, _domainTools, _fileSystemBackendFactory, _filesystemEnabledTools,
                 ct, _enableResourceSubscriptions);
```

- [ ] **Step 4: Update `MultiAgentFactory` to extract filesystem `EnabledTools` and pass to `McpAgent`**

Add a helper method to `MultiAgentFactory`:

```csharp
private static IReadOnlySet<string>? ExtractFilesystemEnabledTools(IEnumerable<string> enabledFeatures)
{
    var fsParts = enabledFeatures
        .Select(f => f.Split('.', 2))
        .Where(p => p[0].Equals("filesystem", StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (fsParts.Count == 0) return new HashSet<string>(); // no filesystem feature = no tools
    if (fsParts.Any(p => p.Length == 1)) return null; // bare "filesystem" = all tools

    return fsParts
        .Where(p => p.Length == 2)
        .Select(p => p[1])
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
```

In both `CreateFromDefinition` and `CreateSubAgent`, extract and pass:

```csharp
var filesystemEnabledTools = ExtractFilesystemEnabledTools(definition.EnabledFeatures);

return new McpAgent(
    definition.McpServerEndpoints,
    definition.FileSystemEndpoints,
    effectiveClient,
    name,
    definition.Description ?? "",
    stateStore,
    userId,
    definition.CustomInstructions,
    domainTools,
    domainPrompts,
    fileSystemBackendFactory: fileSystemBackendFactory,
    filesystemEnabledTools: filesystemEnabledTools);
```

- [ ] **Step 5: Update `ConfigModule.cs`**

In `Agent/Modules/ConfigModule.cs`, add `.AddFileSystem()` to the chain:

```csharp
public static IServiceCollection ConfigureAgents(
    this IServiceCollection services, AgentSettings settings, CommandLineParams cmdParams, IConfiguration config)
{
    return services
        .AddAgent(settings)
        .AddFileSystem()
        .AddScheduling()
        .AddSubAgents(settings.SubAgents)
        .AddMemory(config)
        .AddChatMonitoring(settings, cmdParams);
}
```

- [ ] **Step 6: Update `appsettings.json`**

Move `mcp-text` from `mcpServerEndpoints` to `fileSystemEndpoints` for jonas, add `"filesystem"` to `enabledFeatures`, update `whitelistPatterns`:

For the `jonas` agent entry:
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

For the `jonas-worker` subagent entry, add `fileSystemEndpoints`:
```json
{
    "id": "jonas-worker",
    "name": "Jonas Worker",
    "description": "A worker subagent with the same toolset as Jonas for delegating tasks",
    "model": "z-ai/glm-5:nitro",
    "mcpServerEndpoints": [
        "http://mcp-websearch:8080/mcp",
        "http://mcp-idealista:8080/mcp"
    ],
    "fileSystemEndpoints": [
        "http://mcp-text:8080/mcp"
    ],
    "maxExecutionSeconds": 600
}
```

- [ ] **Step 7: Verify the full solution builds**

Run: `dotnet build`
Expected: Build succeeded

- [ ] **Step 8: Run all non-E2E tests**

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E" --no-restore`
Expected: All tests PASS

- [ ] **Step 9: Commit**

```bash
git add Agent/Modules/FileSystemModule.cs Agent/Modules/ConfigModule.cs \
  Agent/appsettings.json Infrastructure/Agents/ThreadSession.cs \
  Infrastructure/Agents/McpAgent.cs Infrastructure/Agents/MultiAgentFactory.cs
git commit -m "$(cat <<'EOF'
feat: wire filesystem feature into DI and agent configuration

Register IFileSystemBackendFactory in DI. ThreadSessionBuilder creates
FileSystemToolFeature after filesystem discovery and adds tools to the
combined list. Move mcp-text to fileSystemEndpoints in appsettings.
Add filesystem to enabledFeatures for jonas agent.
EOF
)"
```

---

## Task 10: Cleanup Old File/Text Tools

**Files:**
- Delete: `Domain/Tools/Text/TextReadTool.cs`
- Delete: `Domain/Tools/Text/TextEditTool.cs`
- Delete: `Domain/Tools/Text/TextCreateTool.cs`
- Delete: `Domain/Tools/Text/TextSearchTool.cs`
- Delete: `Domain/Tools/Text/TextToolBase.cs`
- Delete: `Domain/Tools/Text/SearchOutputMode.cs`
- Delete: `Domain/Tools/Files/GlobFilesTool.cs`
- Delete: `Domain/Tools/Files/MoveTool.cs`
- Delete: `Domain/Tools/Files/RemoveTool.cs`
- Delete: `Domain/Tools/Files/GlobMode.cs`
- Assess: `Domain/Tools/Config/BaseLibraryPathConfig.cs`
- Delete: Old test files that test the removed classes directly

**Important:** The old domain tools are still used by the `Fs*Tool` classes in McpServerText (they inherit from them). **Do NOT delete them yet.** Only delete them when McpServerText's `Fs*Tool` classes no longer inherit from them — which would be a follow-up refactor where the MCP tools use `IFileSystemClient` directly instead of inheriting.

- [ ] **Step 1: Check if old domain tools have other consumers**

Run: `grep -r "Domain.Tools.Text" --include="*.cs" | grep -v "Tests/" | grep -v "McpServerText/" | grep -v "Domain/Tools/Text/" | grep -v "Domain/Tools/FileSystem/"` and similarly for `Domain.Tools.Files`.

If the only consumers outside of `McpServerText/` and `Tests/` are the old classes themselves, the old test files can be deleted (they test the old domain tools which are now superseded by the new FileSystem tools). But the old domain tool **classes** must stay since McpServerText inherits from them.

- [ ] **Step 2: Delete old test files that are superseded**

```bash
rm Tests/Unit/Domain/Tools/GlobFilesToolTests.cs
rm Tests/Unit/Domain/Text/TextSearchToolTests.cs
```

Check for other old text/file tool test files and remove them too.

- [ ] **Step 3: Run all tests to verify nothing is broken**

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E" --no-restore`
Expected: All tests PASS

- [ ] **Step 4: Commit**

```bash
git add -u Tests/
git commit -m "$(cat <<'EOF'
chore: remove superseded old file/text tool tests

Old domain tool tests are replaced by new FileSystem tool tests.
The old domain tool classes remain as they're inherited by McpServerText
Fs*Tool classes.
EOF
)"
```

---

## Task 11: FileSystemToolFeature Prompt with Dynamic Mounts

**Files:**
- Modify: `Domain/Tools/FileSystem/FileSystemToolFeature.cs` (if the prompt isn't already working from Task 5)
- Modify: `Infrastructure/Agents/ThreadSession.cs` — add filesystem prompts to the session

- [ ] **Step 1: Ensure filesystem prompts reach the LLM**

In `ThreadSessionBuilder.BuildAsync`, after creating `fileSystemTools`, also collect the prompt:

```csharp
IReadOnlyList<string> fileSystemPrompts = [];
if (fileSystemEndpoints.Length > 0 && fileSystemBackendFactory is not null)
{
    registry = new VirtualFileSystemRegistry();
    await registry.DiscoverAsync(fileSystemEndpoints, fileSystemBackendFactory, ct);

    var feature = new FileSystemToolFeature(registry);
    fileSystemTools = feature.GetTools(featureConfig).ToList();
    fileSystemPrompts = feature.Prompt is not null ? [feature.Prompt] : [];
}
```

Update `ThreadSessionData` to include filesystem prompts:

```csharp
internal sealed record ThreadSessionData(
    McpClientManager ClientManager,
    McpResourceManager? ResourceManager,
    IReadOnlyList<AITool> Tools,
    IVirtualFileSystemRegistry? FileSystemRegistry,
    IReadOnlyList<string> FileSystemPrompts);
```

Add a `FileSystemPrompts` property to `ThreadSession`:

```csharp
public IReadOnlyList<string> FileSystemPrompts => _data.FileSystemPrompts;
```

- [ ] **Step 2: Update `McpAgent.CreateRunOptions` to include filesystem prompts**

In `McpAgent.cs`, update `CreateRunOptions`:

```csharp
private ChatClientAgentRunOptions CreateRunOptions(ThreadSession session)
{
    var prompts = _domainPrompts
        .Concat(session.FileSystemPrompts)
        .Concat(session.ClientManager.Prompts)
        .Prepend(BasePrompt.Instructions);

    if (!string.IsNullOrEmpty(_customInstructions))
    {
        prompts = prompts.Prepend(_customInstructions);
    }

    return new ChatClientAgentRunOptions(new ChatOptions
    {
        Tools = [.. session.Tools],
        Instructions = string.Join("\n\n", prompts)
    });
}
```

- [ ] **Step 3: Verify the solution builds**

Run: `dotnet build`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add Infrastructure/Agents/ThreadSession.cs Infrastructure/Agents/McpAgent.cs
git commit -m "$(cat <<'EOF'
feat: include filesystem mount prompts in agent instructions

ThreadSessionBuilder collects the filesystem feature prompt listing
available mount points. McpAgent includes these prompts in the LLM
instructions so the agent knows which filesystem paths are available.
EOF
)"
```

---

## Task 12: Final Verification

- [ ] **Step 1: Full solution build**

Run: `dotnet build`
Expected: Build succeeded with no errors or warnings

- [ ] **Step 2: Run all non-E2E tests**

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E" --no-restore -v normal`
Expected: All tests PASS

- [ ] **Step 3: Verify docker compose still works**

Run: `docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot config --services`
Expected: All services listed, no config errors

- [ ] **Step 4: Commit any remaining fixes**

If any issues were found in steps 1-3, fix them and commit.
