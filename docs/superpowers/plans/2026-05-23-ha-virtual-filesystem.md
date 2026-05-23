# Home Assistant Virtual Filesystem Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose Home Assistant as a virtual filesystem mount (`filesystem://ha` → `/ha`) so the agent navigates and controls it with the same `glob`/`read`/`search`/`exec` verbs it uses for Vault/Sandbox/media, with services modelled as executable `.sh` action files.

**Architecture:** A synthetic (API-backed, not disk-backed) filesystem inside `McpServerHomeAssistant`. A cached `HaCatalog` (entities + services + areas) drives a virtual tree; bespoke `fs_*` MCP tools return the same JSON shapes as the Sandbox/Vault backends so the agent-side pass-through VFS tools surface them unchanged. The agent's `McpFileSystemDiscovery` auto-mounts it — zero agent-side changes. The four typed `home_*` tools are removed; all action flows through `exec`.

**Tech Stack:** .NET 10, C#, `ModelContextProtocol` server SDK, `System.Text.Json.Nodes`, xUnit + Shouldly. Backed by the existing `IHomeAssistantClient` (REST: states, services, call-service, template render).

---

## Spec

`docs/superpowers/specs/2026-05-23-ha-virtual-filesystem-design.md`

## Conventions (read once before starting)

- **Layering:** Pure VFS logic lives in `Domain/Tools/HomeAssistant/Vfs/` (depends only on `IHomeAssistantClient` + DTOs from `Domain.Contracts`). Thin MCP wrappers live in `McpServerHomeAssistant/McpTools/`. This mirrors the existing split (`Domain/Tools/Files/GlobFilesTool.cs` ↔ `McpServerSandbox/McpTools/FsGlobTool.cs`).
- **No trailing newline** in non-test source files under `Domain/`/`McpServer*`. Test files **do** end with a newline.
- **No XML doc comments.** Comment only the non-obvious "why". Prefer LINQ over loops.
- **Records** for DTOs, **primary constructors** for DI, `CancellationToken` on every async method.
- **Errors:** throw for generic failures (the server's `AddCallToolFilter` wraps them); use `Domain.Tools.ToolError.Create(code, message, retryable, hint)` only for envelopes carrying specific recovery guidance. Do NOT add try/catch in MCP tool methods.
- Run all tests from repo root with: `dotnet test Tests/Tests.csproj`. Filter a single test with `--filter "FullyQualifiedName~<ClassName>"`.

## Output shapes the `fs_*` tools must return (match Sandbox/Vault)

| Op | Shape |
|----|-------|
| glob | JSON array of mount-relative path strings, or `{files:[...], truncated:true, total, message}` when >200 (files mode) |
| read | `{filePath, content, totalLines, truncated, suggestion?}` — `content` is line-numbered `"1: ...\n2: ..."` |
| info | `{exists, isDirectory?, path, size?, lastModified?}` |
| exec | `{stdout, stderr, exitCode, truncated}` |
| search | `{query, regex, filesSearched, filesWithMatches, totalMatches, truncated, results:[{file, matches:[{line, text}]}]}` |
| error | `{ok:false, errorCode, message, retryable, hint?}` via `ToolError.Create` |

## File Structure

**Create (Domain — pure logic + engine):**
- `Domain/Tools/HomeAssistant/Vfs/HaCatalog.cs` — snapshot DTO (`HaCatalog`, `HaAreaEntities`) + query helpers.
- `Domain/Tools/HomeAssistant/Vfs/HaVfsPath.cs` — `HaVfsKind`, `HaVfsNode`, structural `Parse`.
- `Domain/Tools/HomeAssistant/Vfs/HaTree.cs` — enumerates all directory/file paths from a catalog; glob matching.
- `Domain/Tools/HomeAssistant/Vfs/HaActionResolver.cs` — class-domain services whose `target` accepts an entity.
- `Domain/Tools/HomeAssistant/Vfs/HaServiceHelpRenderer.cs` — `HaServiceDefinition` → `--help` text.
- `Domain/Tools/HomeAssistant/Vfs/HaArgParser.cs` — GNU flags → `service_data` `JsonObject`.
- `Domain/Tools/HomeAssistant/Vfs/HaStateRenderer.cs` — `HaEntityState` → YAML.
- `Domain/Tools/HomeAssistant/Vfs/HaCatalogProvider.cs` — cached catalog builder (TTL, `Func<IHomeAssistantClient>`, `TimeProvider`).
- `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs` — the engine: `GlobAsync`/`InfoAsync`/`ReadAsync`/`SearchAsync`/`ExecAsync` → `JsonNode`.

**Create (server — MCP wrappers + resource):**
- `McpServerHomeAssistant/McpTools/FsGlobTool.cs`, `FsInfoTool.cs`, `FsReadTool.cs`, `FsSearchTool.cs`, `FsExecTool.cs`.
- `McpServerHomeAssistant/McpResources/FileSystemResource.cs` — `filesystem://ha`.

**Modify:**
- `Domain/Prompts/HomeAssistantSetupSummary.cs` — consume `HaCatalogProvider`; render the slim index instead of the full per-entity dump.
- `Domain/Prompts/HomeAssistantPrompt.cs` — rewrite the workflow for the filesystem idiom.
- `McpServerHomeAssistant/Modules/ConfigModule.cs` — register `HaCatalogProvider`, `HaFileSystem`, the five `fs_*` tools and the resource; remove the four `home_*` tool registrations.

**Delete:**
- `McpServerHomeAssistant/McpTools/McpHome{GetState,ListEntities,ListServices,CallService}Tool.cs` (4).
- `Domain/Tools/HomeAssistant/Home{GetState,ListEntities,ListServices,CallService}Tool.cs` (4) and their tests under `Tests/Unit/Domain/HomeAssistant/`.

**Test (create):** `Tests/Unit/Domain/HomeAssistant/Vfs/` — `FakeHaClient.cs` + one `*Tests.cs` per unit, plus `HaFileSystemJourneyTests.cs`.

---

## Phase 1 — Catalog, path model, tree

### Task 1: `FakeHaClient` test double

**Files:**
- Create: `Tests/Unit/Domain/HomeAssistant/Vfs/FakeHaClient.cs`

A reusable in-memory `IHomeAssistantClient` for every VFS test. No test logic — just a configurable double.

- [ ] **Step 1: Write the file** (no test of its own; it's a fixture used by later tasks)

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

// Configurable in-memory IHomeAssistantClient. `AreaTemplateJson` is the exact JSON the real
// RenderTemplateAsync returns for the area template HaCatalogProvider sends.
public sealed class FakeHaClient : IHomeAssistantClient
{
    public List<HaEntityState> States { get; init; } = [];
    public List<HaServiceDefinition> Services { get; init; } = [];
    public string AreaTemplateJson { get; set; } = """{"areas":[]}""";

    public (string Domain, string Service, string? EntityId, IReadOnlyDictionary<string, JsonNode?>? Data)? LastCall { get; private set; }
    public Func<string, string, string?, IReadOnlyDictionary<string, JsonNode?>?, HaServiceCallResult>? CallHandler { get; set; }

    public Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HaEntityState>>(States);

    public Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default)
        => Task.FromResult(States.FirstOrDefault(s => s.EntityId == entityId));

    public Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HaServiceDefinition>>(Services);

    public Task<HaServiceCallResult> CallServiceAsync(
        string domain, string service, string? entityId,
        IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
    {
        LastCall = (domain, service, entityId, data);
        var result = CallHandler?.Invoke(domain, service, entityId, data)
                     ?? new HaServiceCallResult { ChangedEntities = [] };
        return Task.FromResult(result);
    }

    public Task<string> RenderTemplateAsync(string template, CancellationToken ct = default)
        => Task.FromResult(AreaTemplateJson);

    public static HaEntityState Entity(string id, string state, params (string Key, JsonNode? Value)[] attrs) => new()
    {
        EntityId = id,
        State = state,
        Attributes = attrs.ToDictionary(a => a.Key, a => a.Value),
        LastChanged = DateTimeOffset.Parse("2026-05-23T09:14:02Z"),
        LastUpdated = DateTimeOffset.Parse("2026-05-23T09:14:02Z")
    };

    public static HaServiceDefinition Service(string domain, string service, JsonNode? target, params (string Name, HaServiceField Field)[] fields) => new()
    {
        Domain = domain,
        Service = service,
        Target = target,
        Fields = fields.ToDictionary(f => f.Name, f => f.Field)
    };

    public static JsonNode AnyEntityTarget() => JsonNode.Parse("""{"entity":[{}]}""")!;
    public static JsonNode DomainTarget(string domain) => JsonNode.Parse($$"""{"entity":[{"domain":["{{domain}}"]}]}""")!;
}
```

- [ ] **Step 2: Compile** — Run `dotnet build Tests/Tests.csproj`. Expected: builds (references `IHomeAssistantClient` which already exists).

- [ ] **Step 3: Commit**

```bash
git add Tests/Unit/Domain/HomeAssistant/Vfs/FakeHaClient.cs
git commit -m "test: HA VFS in-memory client double"
```

---

### Task 2: `HaCatalog` snapshot + queries

**Files:**
- Create: `Domain/Tools/HomeAssistant/Vfs/HaCatalog.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaCatalogTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaCatalogTests
{
    private static HaCatalog Sample() => new(
        [Entity("light.kitchen", "off"), Entity("light.salon", "on"), Entity("sensor.salon_temp", "21")],
        [],
        [new HaAreaEntities("salon", "Salón", ["light.salon", "sensor.salon_temp"])]);

    [Fact]
    public void ClassDomains_ReturnsSortedDistinctPrefixes()
    {
        Sample().ClassDomains().ShouldBe(["light", "sensor"]);
    }

    [Fact]
    public void ObjectIdsFor_ReturnsObjectIdsOfThatClass()
    {
        Sample().ObjectIdsFor("light").ShouldBe(["kitchen", "salon"]);
    }

    [Fact]
    public void EntityById_FindsEntity_AndReturnsNullWhenMissing()
    {
        Sample().EntityById("light.kitchen")!.State.ShouldBe("off");
        Sample().EntityById("light.missing").ShouldBeNull();
    }

    [Fact]
    public void AreaSlugs_IncludesUnassignedWhenSomeEntityHasNoArea()
    {
        // light.kitchen is in no area -> "unassigned" bucket appears.
        Sample().AreaSlugs().ShouldBe(["salon", "unassigned"]);
    }

    [Fact]
    public void EntityIdsInArea_ReturnsAssigned_AndUnassignedBucket()
    {
        Sample().EntityIdsInArea("salon").ShouldBe(["light.salon", "sensor.salon_temp"]);
        Sample().EntityIdsInArea("unassigned").ShouldBe(["light.kitchen"]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaCatalogTests"`
Expected: FAIL — `HaCatalog` / `HaAreaEntities` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

public sealed record HaAreaEntities(string Id, string Name, IReadOnlyList<string> EntityIds);

public sealed record HaCatalog(
    IReadOnlyList<HaEntityState> Entities,
    IReadOnlyList<HaServiceDefinition> Services,
    IReadOnlyList<HaAreaEntities> Areas)
{
    public const string UnassignedArea = "unassigned";

    public static HaCatalog Empty { get; } = new([], [], []);

    public IReadOnlyList<string> ClassDomains() => Entities
        .Select(e => ClassOf(e.EntityId))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(d => d, StringComparer.Ordinal)
        .ToList();

    public IReadOnlyList<string> ObjectIdsFor(string classDomain) => Entities
        .Where(e => ClassOf(e.EntityId).Equals(classDomain, StringComparison.Ordinal))
        .Select(e => ObjectOf(e.EntityId))
        .OrderBy(o => o, StringComparer.Ordinal)
        .ToList();

    public HaEntityState? EntityById(string entityId) =>
        Entities.FirstOrDefault(e => e.EntityId.Equals(entityId, StringComparison.Ordinal));

    public IReadOnlyList<string> AreaSlugs()
    {
        var slugs = Areas
            .Where(a => a.EntityIds.Any(AssignedExists))
            .Select(a => a.Id)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        if (Unassigned().Count > 0)
        {
            slugs.Add(UnassignedArea);
        }
        return slugs;
    }

    public IReadOnlyList<string> EntityIdsInArea(string area)
    {
        if (area.Equals(UnassignedArea, StringComparison.Ordinal))
        {
            return Unassigned();
        }
        return Areas
            .FirstOrDefault(a => a.Id.Equals(area, StringComparison.Ordinal))?.EntityIds
            .Where(AssignedExists)
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList() ?? [];
    }

    private bool AssignedExists(string entityId) => EntityById(entityId) is not null;

    private IReadOnlyList<string> Unassigned()
    {
        var assigned = Areas.SelectMany(a => a.EntityIds).ToHashSet(StringComparer.Ordinal);
        return Entities
            .Select(e => e.EntityId)
            .Where(id => !assigned.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    public static string ClassOf(string entityId)
    {
        var dot = entityId.IndexOf('.');
        return dot < 0 ? entityId : entityId[..dot];
    }

    public static string ObjectOf(string entityId)
    {
        var dot = entityId.IndexOf('.');
        return dot < 0 ? entityId : entityId[(dot + 1)..];
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaCatalogTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaCatalog.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaCatalogTests.cs
git commit -m "feat(ha-vfs): catalog snapshot with class/area queries"
```

---

### Task 3: `HaVfsPath` structural parser

**Files:**
- Create: `Domain/Tools/HomeAssistant/Vfs/HaVfsPath.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaVfsPathTests.cs`

Parses a **mount-relative** path (the registry strips `/ha` and the leading `/`) into a typed node. Purely structural — no catalog, no existence check. `EntityId` is normalised: under `entities/` it is `class.object`; under `areas/` the third segment is already a full `entity_id`.

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaVfsPathTests
{
    [Theory]
    [InlineData("", HaVfsKind.Root)]
    [InlineData("entities", HaVfsKind.EntitiesRoot)]
    [InlineData("areas", HaVfsKind.AreasRoot)]
    public void Parse_Roots(string path, HaVfsKind kind) =>
        HaVfsPath.Parse(path).Kind.ShouldBe(kind);

    [Fact]
    public void Parse_ClassDir()
    {
        var n = HaVfsPath.Parse("entities/light");
        n.Kind.ShouldBe(HaVfsKind.ClassDir);
        n.ClassDomain.ShouldBe("light");
    }

    [Fact]
    public void Parse_EntityDir_FromEntitiesRoot()
    {
        var n = HaVfsPath.Parse("entities/light/kitchen");
        n.Kind.ShouldBe(HaVfsKind.EntityDir);
        n.EntityId.ShouldBe("light.kitchen");
    }

    [Fact]
    public void Parse_AreaDir()
    {
        var n = HaVfsPath.Parse("areas/salon");
        n.Kind.ShouldBe(HaVfsKind.AreaDir);
        n.Area.ShouldBe("salon");
    }

    [Fact]
    public void Parse_EntityDir_FromAreasRoot_UsesFullEntityId()
    {
        var n = HaVfsPath.Parse("areas/salon/light.salon");
        n.Kind.ShouldBe(HaVfsKind.EntityDir);
        n.EntityId.ShouldBe("light.salon");
        n.Area.ShouldBe("salon");
    }

    [Fact]
    public void Parse_StateFile()
    {
        var n = HaVfsPath.Parse("entities/light/kitchen/state.yaml");
        n.Kind.ShouldBe(HaVfsKind.StateFile);
        n.EntityId.ShouldBe("light.kitchen");
    }

    [Fact]
    public void Parse_ActionFile_StripsShExtension()
    {
        var n = HaVfsPath.Parse("entities/light/kitchen/turn_on.sh");
        n.Kind.ShouldBe(HaVfsKind.ActionFile);
        n.EntityId.ShouldBe("light.kitchen");
        n.Service.ShouldBe("turn_on");
    }

    [Fact]
    public void Parse_ActionFile_UnderArea()
    {
        var n = HaVfsPath.Parse("areas/salon/light.salon/toggle.sh");
        n.Kind.ShouldBe(HaVfsKind.ActionFile);
        n.EntityId.ShouldBe("light.salon");
        n.Service.ShouldBe("toggle");
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("entities/light/kitchen/extra/deep")]
    [InlineData("areas/salon/light.salon/x/y")]
    public void Parse_Unknown(string path) =>
        HaVfsPath.Parse(path).Kind.ShouldBe(HaVfsKind.Unknown);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaVfsPathTests"`
Expected: FAIL — `HaVfsPath`/`HaVfsKind`/`HaVfsNode` undefined.

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace Domain.Tools.HomeAssistant.Vfs;

public enum HaVfsKind
{
    Root, EntitiesRoot, ClassDir, AreasRoot, AreaDir, EntityDir, StateFile, ActionFile, Unknown
}

public sealed record HaVfsNode(
    HaVfsKind Kind,
    string? ClassDomain = null,
    string? Area = null,
    string? EntityId = null,
    string? Service = null);

public static class HaVfsPath
{
    public const string StateFileName = "state.yaml";

    public static HaVfsNode Parse(string relativePath)
    {
        var segments = (relativePath ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            return new HaVfsNode(HaVfsKind.Root);
        }

        return segments[0] switch
        {
            "entities" => ParseEntities(segments),
            "areas" => ParseAreas(segments),
            _ => new HaVfsNode(HaVfsKind.Unknown)
        };
    }

    private static HaVfsNode ParseEntities(string[] s) => s.Length switch
    {
        1 => new HaVfsNode(HaVfsKind.EntitiesRoot),
        2 => new HaVfsNode(HaVfsKind.ClassDir, ClassDomain: s[1]),
        3 => new HaVfsNode(HaVfsKind.EntityDir, ClassDomain: s[1], EntityId: $"{s[1]}.{s[2]}"),
        4 => Leaf(s[3], $"{s[1]}.{s[2]}", area: null),
        _ => new HaVfsNode(HaVfsKind.Unknown)
    };

    private static HaVfsNode ParseAreas(string[] s) => s.Length switch
    {
        1 => new HaVfsNode(HaVfsKind.AreasRoot),
        2 => new HaVfsNode(HaVfsKind.AreaDir, Area: s[1]),
        3 => new HaVfsNode(HaVfsKind.EntityDir, Area: s[1], EntityId: s[2]),
        4 => Leaf(s[3], s[2], area: s[1]),
        _ => new HaVfsNode(HaVfsKind.Unknown)
    };

    private static HaVfsNode Leaf(string fileName, string entityId, string? area)
    {
        if (fileName.Equals(StateFileName, StringComparison.Ordinal))
        {
            return new HaVfsNode(HaVfsKind.StateFile, Area: area, EntityId: entityId);
        }
        if (fileName.EndsWith(".sh", StringComparison.Ordinal))
        {
            return new HaVfsNode(HaVfsKind.ActionFile, Area: area, EntityId: entityId, Service: fileName[..^3]);
        }
        return new HaVfsNode(HaVfsKind.Unknown);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaVfsPathTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaVfsPath.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaVfsPathTests.cs
git commit -m "feat(ha-vfs): structural virtual-path parser"
```

---

### Task 4: `HaActionResolver` (target matching)

**Files:**
- Create: `Domain/Tools/HomeAssistant/Vfs/HaActionResolver.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaActionResolverTests.cs`

Returns the **class-domain** services whose `target` accepts an entity, sorted by service name.

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.Contracts;
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaActionResolverTests
{
    private static readonly List<HaServiceDefinition> Services =
    [
        Service("light", "turn_on", AnyEntityTarget()),
        Service("light", "toggle", DomainTarget("light")),
        Service("light", "no_target", null),                 // not entity-targeted
        Service("vacuum", "start", DomainTarget("vacuum")),  // wrong class domain
        Service("homeassistant", "restart", null)
    ];

    [Fact]
    public void ServicesFor_ReturnsClassDomainTargetedServices_Sorted()
    {
        var result = HaActionResolver.ServicesFor("light.kitchen", Services)
            .Select(s => s.Service).ToList();
        result.ShouldBe(["toggle", "turn_on"]);
    }

    [Fact]
    public void ServicesFor_DomainNarrowedToOtherClass_Excluded()
    {
        HaActionResolver.ServicesFor("light.kitchen", Services)
            .ShouldNotContain(s => s.Service == "start");
    }

    [Fact]
    public void ServicesFor_ReadOnlyEntity_ReturnsEmpty()
    {
        // sensor has no class-domain entity-targeted services here.
        HaActionResolver.ServicesFor("sensor.salon_temp", Services).ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaActionResolverTests"`
Expected: FAIL — `HaActionResolver` undefined.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaActionResolver
{
    public static IReadOnlyList<HaServiceDefinition> ServicesFor(
        string entityId, IReadOnlyList<HaServiceDefinition> services)
    {
        var classDomain = HaCatalog.ClassOf(entityId);
        return services
            .Where(s => s.Domain.Equals(classDomain, StringComparison.Ordinal))
            .Where(s => TargetAcceptsEntity(s.Target, classDomain))
            .OrderBy(s => s.Service, StringComparer.Ordinal)
            .ToList();
    }

    // target == null  -> not entity-targeted, exclude.
    // target has no "entity" constraint we can read -> accept (it targets entities generically).
    // target.entity is a list of selector objects; accept if any has no "domain" narrowing,
    // or a "domain" list/string that includes the entity's class.
    private static bool TargetAcceptsEntity(JsonNode? target, string classDomain)
    {
        if (target is null)
        {
            return false;
        }
        if (target["entity"] is not JsonNode entity)
        {
            return true;
        }

        var selectors = entity as JsonArray ?? [entity.DeepClone()];
        return selectors.Any(sel => SelectorAcceptsDomain(sel, classDomain));
    }

    private static bool SelectorAcceptsDomain(JsonNode? selector, string classDomain)
    {
        if (selector?["domain"] is not JsonNode domain)
        {
            return true;
        }
        return domain switch
        {
            JsonArray arr => arr.Any(d => d?.GetValue<string>() == classDomain),
            JsonValue v => v.GetValue<string>() == classDomain,
            _ => false
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaActionResolverTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaActionResolver.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaActionResolverTests.cs
git commit -m "feat(ha-vfs): resolve class-domain actions via target matching"
```

---

### Task 5: `HaTree` enumeration + glob

**Files:**
- Create: `Domain/Tools/HomeAssistant/Vfs/HaTree.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaTreeTests.cs`

Enumerates every directory/file path in the virtual tree from a catalog (both `entities/` and `areas/` roots), and matches a `basePath`+`pattern` glob against them. `*` = one segment, `**` = any depth, `?` = one char.

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaTreeTests
{
    private static HaCatalog Cat() => new(
        [Entity("light.kitchen", "off"), Entity("sensor.salon_temp", "21")],
        [Service("light", "turn_on", AnyEntityTarget())],
        [new HaAreaEntities("salon", "Salón", ["sensor.salon_temp"])]);

    [Fact]
    public void Directories_IncludeRootsClassesEntitiesAndAreas()
    {
        var dirs = HaTree.Directories(Cat());
        dirs.ShouldContain("entities");
        dirs.ShouldContain("entities/light");
        dirs.ShouldContain("entities/light/kitchen");
        dirs.ShouldContain("areas");
        dirs.ShouldContain("areas/salon");
        dirs.ShouldContain("areas/salon/sensor.salon_temp");
        dirs.ShouldContain("areas/unassigned/light.kitchen");
    }

    [Fact]
    public void Files_IncludeStateAndApplicableActions()
    {
        var files = HaTree.Files(Cat());
        files.ShouldContain("entities/light/kitchen/state.yaml");
        files.ShouldContain("entities/light/kitchen/turn_on.sh");
        files.ShouldContain("entities/sensor/salon_temp/state.yaml");
        files.ShouldNotContain("entities/sensor/salon_temp/turn_on.sh"); // no actions for sensor
    }

    [Fact]
    public void Glob_Directories_StarMatchesOneSegment()
    {
        var hits = HaTree.Glob(Cat(), "entities/light", "*", directories: true);
        hits.ShouldBe(["entities/light/kitchen"]);
    }

    [Fact]
    public void Glob_Files_DoubleStarRecurses()
    {
        var hits = HaTree.Glob(Cat(), "entities", "**/*.sh", directories: false);
        hits.ShouldBe(["entities/light/kitchen/turn_on.sh"]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaTreeTests"`
Expected: FAIL — `HaTree` undefined.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Text.RegularExpressions;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaTree
{
    public static IReadOnlyList<string> Directories(HaCatalog catalog)
    {
        var dirs = new List<string> { "entities", "areas" };

        dirs.AddRange(catalog.ClassDomains().Select(c => $"entities/{c}"));
        dirs.AddRange(catalog.Entities.Select(e =>
            $"entities/{HaCatalog.ClassOf(e.EntityId)}/{HaCatalog.ObjectOf(e.EntityId)}"));

        foreach (var area in catalog.AreaSlugs())
        {
            dirs.Add($"areas/{area}");
            dirs.AddRange(catalog.EntityIdsInArea(area).Select(id => $"areas/{area}/{id}"));
        }

        return dirs.OrderBy(d => d, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<string> Files(HaCatalog catalog)
    {
        var files = new List<string>();

        foreach (var e in catalog.Entities)
        {
            var entDir = $"entities/{HaCatalog.ClassOf(e.EntityId)}/{HaCatalog.ObjectOf(e.EntityId)}";
            files.AddRange(LeafFiles(entDir, e.EntityId, catalog));
        }

        foreach (var area in catalog.AreaSlugs())
        {
            foreach (var id in catalog.EntityIdsInArea(area))
            {
                files.AddRange(LeafFiles($"areas/{area}/{id}", id, catalog));
            }
        }

        return files.OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> LeafFiles(string entityDir, string entityId, HaCatalog catalog)
    {
        yield return $"{entityDir}/{HaVfsPath.StateFileName}";
        foreach (var svc in HaActionResolver.ServicesFor(entityId, catalog.Services))
        {
            yield return $"{entityDir}/{svc.Service}.sh";
        }
    }

    public static IReadOnlyList<string> Glob(HaCatalog catalog, string basePath, string pattern, bool directories)
    {
        var pool = directories ? Directories(catalog) : Files(catalog);
        var prefix = string.IsNullOrEmpty(basePath) ? string.Empty : basePath.Trim('/') + "/";
        var regex = GlobToRegex(prefix + pattern);
        return pool.Where(p => regex.IsMatch(p)).ToList();
    }

    private static Regex GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
            }
            else
            {
                sb.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString())
                });
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaTreeTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaTree.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaTreeTests.cs
git commit -m "feat(ha-vfs): virtual tree enumeration and glob"
```

---

## Phase 2 — Renderers (state, help, args)

### Task 6: `HaStateRenderer`

**Files:**
- Create: `Domain/Tools/HomeAssistant/Vfs/HaStateRenderer.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaStateRendererTests.cs`

Renders an entity as YAML. Every value is emitted as compact JSON (a valid YAML subset), so arbitrary nested attributes are safe.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json.Nodes;
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaStateRendererTests
{
    [Fact]
    public void ToYaml_RendersScalarsAndAttributes()
    {
        var entity = Entity("light.kitchen", "off",
            ("friendly_name", JsonValue.Create("Kitchen")),
            ("brightness", JsonValue.Create((int?)null)),
            ("modes", JsonNode.Parse("""["color_temp","xy"]""")));

        var yaml = HaStateRenderer.ToYaml(entity);

        yaml.ShouldContain("entity_id: light.kitchen");
        yaml.ShouldContain("state: \"off\"");
        yaml.ShouldContain("last_changed: 2026-05-23T09:14:02");
        yaml.ShouldContain("attributes:");
        yaml.ShouldContain("  brightness: null");
        yaml.ShouldContain("  friendly_name: \"Kitchen\"");
        yaml.ShouldContain("""  modes: ["color_temp","xy"]""");
    }

    [Fact]
    public void ToYaml_NoAttributes_EmitsEmptyMap()
    {
        HaStateRenderer.ToYaml(Entity("sun.sun", "above_horizon"))
            .ShouldContain("attributes: {}");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaStateRendererTests"`
Expected: FAIL — `HaStateRenderer` undefined.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaStateRenderer
{
    public static string ToYaml(HaEntityState entity)
    {
        var sb = new StringBuilder();
        sb.Append("entity_id: ").Append(entity.EntityId).Append('\n');
        sb.Append("state: ").Append(JsonValue.Create(entity.State).ToJsonString()).Append('\n');
        if (entity.LastChanged is { } changed)
        {
            sb.Append("last_changed: ").Append(changed.ToString("O")).Append('\n');
        }
        if (entity.LastUpdated is { } updated)
        {
            sb.Append("last_updated: ").Append(updated.ToString("O")).Append('\n');
        }

        if (entity.Attributes.Count == 0)
        {
            sb.Append("attributes: {}");
            return sb.ToString();
        }

        sb.Append("attributes:");
        foreach (var (key, value) in entity.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            sb.Append("\n  ").Append(key).Append(": ").Append(value?.ToJsonString() ?? "null");
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaStateRendererTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaStateRenderer.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaStateRendererTests.cs
git commit -m "feat(ha-vfs): YAML state renderer"
```

---

### Task 7: `HaServiceHelpRenderer`

**Files:**
- Create: `Domain/Tools/HomeAssistant/Vfs/HaServiceHelpRenderer.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaServiceHelpRendererTests.cs`

Renders the `--help` / `cat` text for an action file from the HA service schema. Field type comes from the `selector` (number → `INT`/`FLOAT` with range, select → options, boolean → `BOOL`, text → `TEXT`, object → `JSON`).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaServiceHelpRendererTests
{
    private static HaServiceField Field(string? desc, bool required, JsonNode? selector) =>
        new() { Description = desc, Required = required, Selector = selector };

    [Fact]
    public void Render_HeaderFieldsAndTypes()
    {
        var svc = Service("light", "turn_on", AnyEntityTarget(),
            ("brightness_pct", Field("Brightness", false, JsonNode.Parse("""{"number":{"min":1,"max":100}}"""))),
            ("flash", Field(null, false, JsonNode.Parse("""{"select":{"options":["short","long"]}}"""))));

        var help = HaServiceHelpRenderer.Render("light.kitchen", svc);

        help.ShouldContain("turn_on.sh — call light.turn_on on light.kitchen");
        help.ShouldContain("--brightness_pct");
        help.ShouldContain("1-100");
        help.ShouldContain("--flash");
        help.ShouldContain("short");
    }

    [Fact]
    public void Render_NoFields_SaysNoArguments()
    {
        HaServiceHelpRenderer.Render("light.kitchen", Service("light", "toggle", AnyEntityTarget()))
            .ShouldContain("(no arguments)");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaServiceHelpRendererTests"`
Expected: FAIL — `HaServiceHelpRenderer` undefined.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaServiceHelpRenderer
{
    public static string Render(string entityId, HaServiceDefinition svc)
    {
        var sb = new StringBuilder();
        sb.Append(svc.Service).Append(".sh — call ")
          .Append(svc.Domain).Append('.').Append(svc.Service)
          .Append(" on ").Append(entityId).Append('\n');

        if (!string.IsNullOrWhiteSpace(svc.Description))
        {
            sb.Append(svc.Description!.Trim()).Append('\n');
        }

        if (svc.Fields.Count == 0)
        {
            sb.Append("  (no arguments)");
            return sb.ToString();
        }

        foreach (var (name, field) in svc.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            sb.Append("  --").Append(name).Append("  ").Append(TypeOf(field.Selector));
            if (field.Required)
            {
                sb.Append("  (required)");
            }
            if (!string.IsNullOrWhiteSpace(field.Description))
            {
                sb.Append("  ").Append(field.Description!.Trim());
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    public static string TypeOf(JsonNode? selector)
    {
        if (selector is null)
        {
            return "TEXT";
        }
        if (selector["number"] is JsonNode number)
        {
            var kind = number["step"]?.GetValue<double>() is { } step && step % 1 != 0 ? "FLOAT" : "INT";
            var min = number["min"];
            var max = number["max"];
            return min is not null && max is not null ? $"{kind} {min}-{max}" : kind;
        }
        if (selector["boolean"] is not null)
        {
            return "BOOL";
        }
        if (selector["select"]?["options"] is JsonArray options)
        {
            var opts = options.Select(o => o is JsonObject obj ? obj["value"]?.ToString() : o?.ToString());
            return $"ONE OF [{string.Join(",", opts)}]";
        }
        if (selector["object"] is not null)
        {
            return "JSON";
        }
        return "TEXT";
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaServiceHelpRendererTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaServiceHelpRenderer.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaServiceHelpRendererTests.cs
git commit -m "feat(ha-vfs): --help renderer from service schema"
```

---

### Task 8: `HaArgParser`

**Files:**
- Create: `Domain/Tools/HomeAssistant/Vfs/HaArgParser.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaArgParserTests.cs`

Parses GNU-flag tokens into `service_data`, coercing each value by the field selector (number/bool/list/object/text). Throws `ArgumentException` on an unknown flag or a malformed JSON object value.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaArgParserTests
{
    private static HaServiceField Field(JsonNode? selector) =>
        new() { Selector = selector };

    private static HaServiceDefinition Svc() => Service("light", "turn_on", AnyEntityTarget(),
        ("brightness_pct", Field(JsonNode.Parse("""{"number":{"min":1,"max":100}}"""))),
        ("on", Field(JsonNode.Parse("""{"boolean":{}}"""))),
        ("modes", Field(JsonNode.Parse("""{"select":{"multiple":true,"options":["a","b"]}}"""))),
        ("advanced", Field(JsonNode.Parse("""{"object":{}}"""))),
        ("name", Field(JsonNode.Parse("""{"text":{}}"""))));

    [Fact]
    public void Parse_CoercesBySelectorType()
    {
        var data = HaArgParser.Parse(
            ["--brightness_pct", "60", "--on", "true", "--modes", "a,b", "--advanced", """{"eco":true}""", "--name", "Lamp"],
            Svc());

        data["brightness_pct"]!.GetValue<int>().ShouldBe(60);
        data["on"]!.GetValue<bool>().ShouldBeTrue();
        ((JsonArray)data["modes"]!).Count.ShouldBe(2);
        data["advanced"]!["eco"]!.GetValue<bool>().ShouldBeTrue();
        data["name"]!.GetValue<string>().ShouldBe("Lamp");
    }

    [Fact]
    public void Parse_UnknownFlag_Throws()
    {
        Should.Throw<ArgumentException>(() => HaArgParser.Parse(["--nope", "1"], Svc()))
            .Message.ShouldContain("nope");
    }

    [Fact]
    public void Parse_Empty_ReturnsEmptyObject()
    {
        HaArgParser.Parse([], Svc()).Count.ShouldBe(0);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaArgParserTests"`
Expected: FAIL — `HaArgParser` undefined.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaArgParser
{
    public static JsonObject Parse(IReadOnlyList<string> tokens, HaServiceDefinition svc)
    {
        var data = new JsonObject();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Expected a --flag but found '{token}'.");
            }

            var name = token[2..];
            if (!svc.Fields.TryGetValue(name, out var field))
            {
                throw new ArgumentException(
                    $"Unknown argument '--{name}'. Run `{svc.Service}.sh --help` for the field list.");
            }

            if (i + 1 >= tokens.Count)
            {
                throw new ArgumentException($"Missing value for '--{name}'.");
            }

            data[name] = Coerce(name, tokens[++i], field.Selector);
        }
        return data;
    }

    private static JsonNode? Coerce(string name, string raw, JsonNode? selector)
    {
        if (selector?["number"] is not null)
        {
            // JsonNode.Parse yields a JsonElement-backed value whose GetValue<int>()/GetValue<double>()
            // both work; JsonValue.Create(long/double) does NOT convert across numeric types on .NET 10.
            return double.TryParse(raw, CultureInfo.InvariantCulture, out _)
                ? JsonNode.Parse(raw)
                : throw new ArgumentException($"--{name} expects a number, got '{raw}'.");
        }
        if (selector?["boolean"] is not null)
        {
            return JsonValue.Create(bool.Parse(raw));
        }
        if (IsMultiSelect(selector))
        {
            return new JsonArray(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }
        if (selector?["object"] is not null)
        {
            try
            {
                return JsonNode.Parse(raw);
            }
            catch (JsonException)
            {
                throw new ArgumentException($"--{name} expects a JSON value, got '{raw}'.");
            }
        }
        return JsonValue.Create(raw);
    }

    private static bool IsMultiSelect(JsonNode? selector) =>
        selector?["select"]?["multiple"]?.GetValue<bool>() == true;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaArgParserTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaArgParser.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaArgParserTests.cs
git commit -m "feat(ha-vfs): GNU-flag argument parser"
```

---

## Phase 3 — Engine + catalog provider

### Task 9: `HaCatalogProvider` (cached builder)

**Files:**
- Create: `Domain/Tools/HomeAssistant/Vfs/HaCatalogProvider.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaCatalogProviderTests.cs`

Builds the catalog from three HA calls (states, services, area template render) and caches it with a TTL. Mirrors `HomeAssistantSetupSummary`'s resilience: on any failure, returns `HaCatalog.Empty` with a short negative-cache TTL. Uses the same area template.

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.Contracts;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaCatalogProviderTests
{
    [Fact]
    public async Task GetAsync_BuildsCatalogFromClient()
    {
        var client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off") },
            Services = { Service("light", "turn_on", AnyEntityTarget()) },
            AreaTemplateJson = """{"areas":[{"id":"salon","name":"Salón","entities":["light.kitchen"]}]}"""
        };
        var provider = new HaCatalogProvider(() => client, new FakeTimeProvider());

        var catalog = await provider.GetAsync(CancellationToken.None);

        catalog.Entities.Count.ShouldBe(1);
        catalog.Services.Count.ShouldBe(1);
        catalog.Areas.ShouldContain(a => a.Id == "salon" && a.EntityIds.Contains("light.kitchen"));
    }

    [Fact]
    public async Task GetAsync_CachesWithinTtl()
    {
        var client = new CountingClient { States = { Entity("light.kitchen", "off") } };
        var provider = new HaCatalogProvider(() => client, new FakeTimeProvider());

        await provider.GetAsync(CancellationToken.None);
        await provider.GetAsync(CancellationToken.None);

        client.StateCalls.ShouldBe(1);
    }

    [Fact]
    public async Task GetAsync_OnFailure_ReturnsEmpty()
    {
        var provider = new HaCatalogProvider(() => new ThrowingClient(), new FakeTimeProvider());
        (await provider.GetAsync(CancellationToken.None)).Entities.ShouldBeEmpty();
    }

    private sealed class CountingClient : FakeHaClient
    {
        public int StateCalls { get; private set; }
        public override Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
        {
            StateCalls++;
            return base.ListStatesAsync(ct);
        }
    }

    private sealed class ThrowingClient : FakeHaClient
    {
        public override Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("HA down");
    }
}
```

> Note: this requires `FakeHaClient` to be non-`sealed` with `public virtual` interface methods so the subclasses can `override` them (and so the override is invoked when `HaCatalogProvider` calls through the `IHomeAssistantClient` interface). This is already done in Task 1 — `FakeHaClient` is declared `public class` with `public virtual` methods. The subclasses use `override`, and the test references `HaEntityState` unqualified via `using Domain.Contracts;` (a fully-qualified `Domain.Contracts.HaEntityState` would mis-resolve against the enclosing `Tests.Unit.Domain` namespace).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaCatalogProviderTests"`
Expected: FAIL — `HaCatalogProvider` undefined.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

// Cached source of truth for both the VFS engine and the slim index prompt. Caches the catalog
// for `CacheTtl`; on any HA failure returns HaCatalog.Empty with a short negative TTL so a
// transient outage doesn't blind the agent for the full window. Func<IHomeAssistantClient> (not a
// direct injection) keeps the transient, IHttpClientFactory-managed client from being pinned for
// the singleton's lifetime — same rationale as HomeAssistantSetupSummary.
public sealed class HaCatalogProvider(Func<IHomeAssistantClient> clientFactory, TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(30);

    // Single template render returns one JSON object covering every area and its entities —
    // the REST API has no other path into the area registry.
    private const string AreaTemplate =
        """{"areas":[{% for aid in areas() %}{% if not loop.first %},{% endif %}{"id":{{aid|tojson}},"name":{{area_name(aid)|tojson}},"entities":{{area_entities(aid)|list|tojson}}}{% endfor %}]}""";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HaCatalog _cached = HaCatalog.Empty;
    private DateTimeOffset _expiry = DateTimeOffset.MinValue;

    public async Task<HaCatalog> GetAsync(CancellationToken ct)
    {
        if (_time.GetUtcNow() < _expiry)
        {
            return _cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_time.GetUtcNow() < _expiry)
            {
                return _cached;
            }

            _cached = await TryBuildAsync(ct);
            _expiry = _time.GetUtcNow() + (_cached.Entities.Count == 0 ? FailureCacheTtl : CacheTtl);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HaCatalog> TryBuildAsync(CancellationToken ct)
    {
        try
        {
            var client = clientFactory();
            var states = client.ListStatesAsync(ct);
            var services = client.ListServicesAsync(ct);
            var areas = LoadAreasAsync(client, ct);
            await Task.WhenAll(states, services, areas);
            return new HaCatalog(states.Result, services.Result, areas.Result);
        }
        catch
        {
            return HaCatalog.Empty;
        }
    }

    private static async Task<IReadOnlyList<HaAreaEntities>> LoadAreasAsync(IHomeAssistantClient client, CancellationToken ct)
    {
        var rendered = await client.RenderTemplateAsync(AreaTemplate, ct);
        if (string.IsNullOrWhiteSpace(rendered))
        {
            return [];
        }
        try
        {
            var payload = JsonSerializer.Deserialize<AreaPayload>(rendered);
            return payload?.Areas?
                .Select(a => new HaAreaEntities(a.Id, a.Name, a.Entities ?? []))
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record AreaPayload
    {
        [JsonPropertyName("areas")] public IReadOnlyList<AreaDto>? Areas { get; init; }
    }

    private sealed record AreaDto
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("entities")] public IReadOnlyList<string>? Entities { get; init; }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaCatalogProviderTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaCatalogProvider.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaCatalogProviderTests.cs Tests/Unit/Domain/HomeAssistant/Vfs/FakeHaClient.cs
git commit -m "feat(ha-vfs): cached catalog provider"
```

---

### Task 10: `HaFileSystem` engine — glob + info + read + search

**Files:**
- Create: `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs`

The engine produces the agent-facing JSON shapes. This task covers everything except `exec` (Task 11). Reads of `state.yaml` go to `GetStateAsync` (always fresh); reads of `.sh` render help from the cached catalog.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json.Nodes;
using Domain.Tools.Files;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaFileSystemReadTests
{
    private static HaFileSystem Build(out FakeHaClient client)
    {
        client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))) },
            Services = { Service("light", "turn_on", AnyEntityTarget()) },
            AreaTemplateJson = """{"areas":[]}"""
        };
        var local = client;
        var provider = new HaCatalogProvider(() => local, new FakeTimeProvider());
        return new HaFileSystem(provider, () => local);
    }

    [Fact]
    public async Task GlobAsync_Directories_ListsEntities()
    {
        var fs = Build(out _);
        var result = (JsonArray)await fs.GlobAsync("entities/light", "*", GlobMode.Directories, CancellationToken.None);
        result.Select(n => n!.GetValue<string>()).ShouldContain("entities/light/kitchen");
    }

    [Fact]
    public async Task InfoAsync_EntityDir_Exists()
    {
        var fs = Build(out _);
        var info = await fs.InfoAsync("entities/light/kitchen", CancellationToken.None);
        info["exists"]!.GetValue<bool>().ShouldBeTrue();
        info["isDirectory"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task InfoAsync_MissingEntity_ExistsFalse()
    {
        var fs = Build(out _);
        (await fs.InfoAsync("entities/light/ghost", CancellationToken.None))["exists"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task ReadAsync_StateFile_RendersFreshYaml()
    {
        var fs = Build(out _);
        var read = await fs.ReadAsync("entities/light/kitchen/state.yaml", null, null, CancellationToken.None);
        read["content"]!.GetValue<string>().ShouldContain("entity_id: light.kitchen");
        read["content"]!.GetValue<string>().ShouldContain("1: ");
    }

    [Fact]
    public async Task ReadAsync_ActionFile_RendersHelp()
    {
        var fs = Build(out _);
        var read = await fs.ReadAsync("entities/light/kitchen/turn_on.sh", null, null, CancellationToken.None);
        read["content"]!.GetValue<string>().ShouldContain("call light.turn_on on light.kitchen");
    }

    [Fact]
    public async Task SearchAsync_FindsEntityByState()
    {
        var fs = Build(out _);
        var result = await fs.SearchAsync("off", false, null, null, null, 50, 1, CancellationToken.None);
        result["totalMatches"]!.GetValue<int>().ShouldBeGreaterThan(0);
        result["results"]!.AsArray().Count.ShouldBeGreaterThan(0);
        result["results"]![0]!["file"]!.GetValue<string>().ShouldContain("light/kitchen");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaFileSystemReadTests"`
Expected: FAIL — `HaFileSystem` undefined.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.Tools.Files;

namespace Domain.Tools.HomeAssistant.Vfs;

public sealed partial class HaFileSystem(HaCatalogProvider catalogProvider, Func<IHomeAssistantClient> clientFactory)
{
    private const int FileResultCap = 200;

    public async Task<JsonNode> GlobAsync(string basePath, string pattern, GlobMode mode, CancellationToken ct)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        var hits = HaTree.Glob(catalog, basePath, pattern, mode == GlobMode.Directories);

        if (mode == GlobMode.Files && hits.Count > FileResultCap)
        {
            return new JsonObject
            {
                ["files"] = new JsonArray(hits.Take(FileResultCap).Select(h => (JsonNode?)h).ToArray()),
                ["truncated"] = true,
                ["total"] = hits.Count,
                ["message"] = $"Showing {FileResultCap} of {hits.Count} matches. Use a more specific pattern."
            };
        }
        return new JsonArray(hits.Select(h => (JsonNode?)h).ToArray());
    }

    public async Task<JsonNode> InfoAsync(string path, CancellationToken ct)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        var node = HaVfsPath.Parse(path);
        var (exists, isDir) = Resolve(node, catalog);

        var result = new JsonObject { ["exists"] = exists, ["path"] = path };
        if (exists)
        {
            result["isDirectory"] = isDir;
        }
        return result;
    }

    public async Task<JsonNode> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        var node = HaVfsPath.Parse(path);
        return node.Kind switch
        {
            HaVfsKind.StateFile => await ReadStateAsync(path, node.EntityId!, offset, limit, ct),
            HaVfsKind.ActionFile => await ReadActionAsync(path, node, ct),
            _ => NotFound(path)
        };
    }

    public async Task<JsonNode> SearchAsync(
        string query, bool regex, string? path, string? directoryPath, string? filePattern,
        int maxResults, int contextLines, CancellationToken ct)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        var matcher = regex
            ? new Regex(query, RegexOptions.IgnoreCase)
            : new Regex(Regex.Escape(query), RegexOptions.IgnoreCase);

        var results = new JsonArray();
        var totalMatches = 0;
        var filesWithMatches = 0;

        foreach (var entity in catalog.Entities)
        {
            var file = $"entities/{HaCatalog.ClassOf(entity.EntityId)}/{HaCatalog.ObjectOf(entity.EntityId)}/{HaVfsPath.StateFileName}";
            var lines = HaStateRenderer.ToYaml(entity).Split('\n');
            var matches = lines
                .Select((text, i) => (text, line: i + 1))
                .Where(l => matcher.IsMatch(l.text))
                .Select(l => new JsonObject { ["line"] = l.line, ["text"] = l.text } as JsonNode)
                .ToList();

            if (matches.Count == 0)
            {
                continue;
            }
            filesWithMatches++;
            totalMatches += matches.Count;
            if (results.Count < maxResults)
            {
                results.Add(new JsonObject { ["file"] = file, ["matches"] = new JsonArray(matches.ToArray()) });
            }
        }

        return new JsonObject
        {
            ["query"] = query,
            ["regex"] = regex,
            ["filesSearched"] = catalog.Entities.Count,
            ["filesWithMatches"] = filesWithMatches,
            ["totalMatches"] = totalMatches,
            ["truncated"] = filesWithMatches > maxResults,
            ["results"] = results
        };
    }

    private async Task<JsonNode> ReadStateAsync(string path, string entityId, int? offset, int? limit, CancellationToken ct)
    {
        var entity = await clientFactory().GetStateAsync(entityId, ct);
        return entity is null ? NotFound(path) : BuildReadResult(path, HaStateRenderer.ToYaml(entity), offset, limit);
    }

    private async Task<JsonNode> ReadActionAsync(string path, HaVfsNode node, CancellationToken ct)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        var svc = HaActionResolver.ServicesFor(node.EntityId!, catalog.Services)
            .FirstOrDefault(s => s.Service.Equals(node.Service, StringComparison.Ordinal));
        return svc is null
            ? NotFound(path)
            : BuildReadResult(path, HaServiceHelpRenderer.Render(node.EntityId!, svc), null, null);
    }

    private static (bool Exists, bool IsDir) Resolve(HaVfsNode node, HaCatalog catalog) => node.Kind switch
    {
        HaVfsKind.Root or HaVfsKind.EntitiesRoot or HaVfsKind.AreasRoot => (true, true),
        HaVfsKind.ClassDir => (catalog.ClassDomains().Contains(node.ClassDomain), true),
        HaVfsKind.AreaDir => (catalog.AreaSlugs().Contains(node.Area), true),
        HaVfsKind.EntityDir => (catalog.EntityById(node.EntityId!) is not null, true),
        HaVfsKind.StateFile => (catalog.EntityById(node.EntityId!) is not null, false),
        HaVfsKind.ActionFile => (HaActionResolver.ServicesFor(node.EntityId!, catalog.Services)
            .Any(s => s.Service == node.Service), false),
        _ => (false, false)
    };

    // Line-numbered read result matching the Sandbox/Vault text_read shape.
    private static JsonNode BuildReadResult(string filePath, string text, int? offset, int? limit)
    {
        var allLines = text.Split('\n');
        var start = Math.Clamp((offset ?? 1) - 1, 0, allLines.Length);
        var remaining = allLines.Skip(start).ToArray();
        var take = Math.Min(limit ?? remaining.Length, remaining.Length);
        var content = string.Join("\n", remaining.Take(take).Select((l, i) => $"{start + i + 1}: {l}"));

        var result = new JsonObject
        {
            ["filePath"] = filePath,
            ["content"] = content,
            ["totalLines"] = allLines.Length,
            ["truncated"] = take < remaining.Length
        };
        if (take < remaining.Length)
        {
            result["suggestion"] = $"Use offset={start + take + 1} to continue reading.";
        }
        return result;
    }

    private static JsonObject NotFound(string path) =>
        Domain.Tools.ToolError.Create(Domain.Tools.ToolError.Codes.NotFound, $"No such path: {path}");
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaFileSystemReadTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs
git commit -m "feat(ha-vfs): engine glob/info/read/search"
```

---

### Task 11: `HaFileSystem.ExecAsync` (the `.sh` action channel)

**Files:**
- Create: `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.Exec.cs` (partial-class continuation)
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemExecTests.cs`

Exec semantics: CWD must be an entity dir; command's first token names an existing `.sh`; `--help` returns help; otherwise parse args and call the service. Exit codes: `0` success/help, `1` HA call failure, `2` arg error, `127` unknown command / not an entity dir.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Exceptions;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaFileSystemExecTests
{
    private static HaFileSystem Build(out FakeHaClient client)
    {
        client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off") },
            Services = { Service("light", "turn_on", AnyEntityTarget(),
                ("brightness_pct", new HaServiceField { Selector = JsonNode.Parse("""{"number":{"min":1,"max":100}}""") })) }
        };
        var local = client;
        var provider = new HaCatalogProvider(() => local, new FakeTimeProvider());
        return new HaFileSystem(provider, () => local);
    }

    [Fact]
    public async Task Exec_CallsService_WithParsedData()
    {
        var fs = Build(out var client);
        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh --brightness_pct 60", null, CancellationToken.None);

        result["exitCode"]!.GetValue<int>().ShouldBe(0);
        client.LastCall!.Value.Domain.ShouldBe("light");
        client.LastCall.Value.Service.ShouldBe("turn_on");
        client.LastCall.Value.EntityId.ShouldBe("light.kitchen");
        client.LastCall.Value.Data!["brightness_pct"]!.GetValue<int>().ShouldBe(60);
    }

    [Fact]
    public async Task Exec_Help_ReturnsUsage_ExitZero_NoCall()
    {
        var fs = Build(out var client);
        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh --help", null, CancellationToken.None);

        result["exitCode"]!.GetValue<int>().ShouldBe(0);
        result["stdout"]!.GetValue<string>().ShouldContain("--brightness_pct");
        client.LastCall.ShouldBeNull();
    }

    [Fact]
    public async Task Exec_DotSlashPrefixAccepted()
    {
        var fs = Build(out var client);
        var result = await fs.ExecAsync("entities/light/kitchen", "./turn_on.sh", null, CancellationToken.None);
        result["exitCode"]!.GetValue<int>().ShouldBe(0);
        client.LastCall.ShouldNotBeNull();
    }

    [Fact]
    public async Task Exec_UnknownCommand_Returns127_WithAvailableActions()
    {
        var fs = Build(out _);
        var result = await fs.ExecAsync("entities/light/kitchen", "cat state.yaml", null, CancellationToken.None);

        result["exitCode"]!.GetValue<int>().ShouldBe(127);
        result["stderr"]!.GetValue<string>().ShouldContain("turn_on.sh");
    }

    [Fact]
    public async Task Exec_BadArg_Returns2()
    {
        var fs = Build(out _);
        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh --nope 1", null, CancellationToken.None);
        result["exitCode"]!.GetValue<int>().ShouldBe(2);
        result["stderr"]!.GetValue<string>().ShouldContain("nope");
    }

    [Fact]
    public async Task Exec_NotAnEntityDir_Returns127()
    {
        var fs = Build(out _);
        var result = await fs.ExecAsync("entities/light", "turn_on.sh", null, CancellationToken.None);
        result["exitCode"]!.GetValue<int>().ShouldBe(127);
    }

    [Fact]
    public async Task Exec_HaFailure_Returns1_WithHint()
    {
        var fs = Build(out var client);
        client.CallHandler = (_, _, _, _) => throw new HomeAssistantException("400 bad field", 400);
        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh --brightness_pct 60", null, CancellationToken.None);

        result["exitCode"]!.GetValue<int>().ShouldBe(1);
        result["stderr"]!.GetValue<string>().ShouldContain("400 bad field");
        result["stderr"]!.GetValue<string>().ShouldContain("--help");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaFileSystemExecTests"`
Expected: FAIL — `ExecAsync` undefined.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Exceptions;

namespace Domain.Tools.HomeAssistant.Vfs;

public sealed partial class HaFileSystem
{
    public async Task<JsonNode> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        var cwd = HaVfsPath.Parse(path);
        if (cwd.Kind != HaVfsKind.EntityDir || catalog.EntityById(cwd.EntityId!) is null)
        {
            return ExecResult(127, "", $"Not an entity directory: {path}. cd into /ha/entities/<class>/<id> first.");
        }

        var tokens = ShellTokenize(command);
        var entityId = cwd.EntityId!;
        var actions = HaActionResolver.ServicesFor(entityId, catalog.Services);
        var available = string.Join(", ", actions.Select(a => $"{a.Service}.sh"));

        if (tokens.Count == 0)
        {
            return ExecResult(127, "", $"No command. Available actions: {available}");
        }

        var script = tokens[0].StartsWith("./", StringComparison.Ordinal) ? tokens[0][2..] : tokens[0];
        if (!script.EndsWith(".sh", StringComparison.Ordinal))
        {
            return ExecResult(127, "", $"command not found: {tokens[0]}. This filesystem only runs action files. Available actions: {available}");
        }

        var serviceName = script[..^3];
        var svc = actions.FirstOrDefault(a => a.Service.Equals(serviceName, StringComparison.Ordinal));
        if (svc is null)
        {
            return ExecResult(127, "", $"command not found: {script}. Available actions: {available}");
        }

        var args = tokens.Skip(1).ToList();
        if (args.Contains("--help") || args.Contains("-h"))
        {
            return ExecResult(0, HaServiceHelpRenderer.Render(entityId, svc), "");
        }

        JsonObject data;
        try
        {
            data = HaArgParser.Parse(args, svc);
        }
        catch (ArgumentException ex)
        {
            return ExecResult(2, "", ex.Message);
        }

        try
        {
            // JsonObject does not satisfy IReadOnlyDictionary<string, JsonNode?> on .NET 10; materialize a
            // detached dictionary first (same pattern as the former HomeCallServiceTool).
            IReadOnlyDictionary<string, JsonNode?> payload = data.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.DeepClone());
            var result = await clientFactory().CallServiceAsync(svc.Domain, svc.Service, entityId, payload, ct);
            var changed = new JsonArray(result.ChangedEntities
                .Select(e => (JsonNode?)$"{e.EntityId} → {e.State}").ToArray());
            var stdout = new JsonObject { ["ok"] = true, ["changed"] = changed };
            if (result.Response is not null)
            {
                stdout["response"] = result.Response.DeepClone();
            }
            return ExecResult(0, stdout.ToJsonString(), "");
        }
        catch (HomeAssistantException ex)
        {
            return ExecResult(1, "",
                $"{ex.Message}\nRe-check the field types with `{serviceName}.sh --help`; don't retry the same shape.");
        }
    }

    private static JsonObject ExecResult(int exitCode, string stdout, string stderr) => new()
    {
        ["stdout"] = stdout,
        ["stderr"] = stderr,
        ["exitCode"] = exitCode,
        ["truncated"] = false
    };

    // Minimal shell tokeniser: whitespace-split, honouring single and double quotes so JSON
    // object values like --advanced '{"eco":true}' survive as one token.
    private static List<string> ShellTokenize(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        var has = false;

        foreach (var c in command)
        {
            if (quote != '\0')
            {
                if (c == quote) { quote = '\0'; }
                else { current.Append(c); }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                has = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (has) { tokens.Add(current.ToString()); current.Clear(); has = false; }
            }
            else
            {
                current.Append(c);
                has = true;
            }
        }
        if (has) { tokens.Add(current.ToString()); }
        return tokens;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaFileSystemExecTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaFileSystem.Exec.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemExecTests.cs
git commit -m "feat(ha-vfs): exec action channel with bash-exit-code guards"
```

---

## Phase 4 — MCP wrappers + resource + DI

### Task 12: The five `fs_*` MCP tool wrappers

**Files:**
- Create: `McpServerHomeAssistant/McpTools/FsGlobTool.cs`, `FsInfoTool.cs`, `FsReadTool.cs`, `FsSearchTool.cs`, `FsExecTool.cs`

Thin wrappers — same shape as `McpServerSandbox/McpTools/Fs*Tool.cs`, but they call `HaFileSystem` (no Domain base class to inherit, since the engine is bespoke). No tests at this layer (logic is covered by the engine tests); they are exercised by the journey test in Task 15.

- [ ] **Step 1: Write `FsGlobTool.cs`**

```csharp
using System.ComponentModel;
using Domain.Tools.Files;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class FsGlobTool(HaFileSystem fs)
{
    [McpServerTool(Name = "fs_glob")]
    [Description("Lists Home Assistant entities, areas, and action files matching a glob pattern. Use mode 'directories' to explore (domains, entities, areas), 'files' to find state.yaml and *.sh action files.")]
    public async Task<CallToolResult> McpRun(
        string pattern,
        string mode = "directories",
        string basePath = "",
        CancellationToken cancellationToken = default)
    {
        var globMode = mode.Equals("files", StringComparison.OrdinalIgnoreCase) ? GlobMode.Files : GlobMode.Directories;
        return ToolResponse.Create(await fs.GlobAsync(basePath, pattern, globMode, cancellationToken));
    }
}
```

- [ ] **Step 2: Write `FsInfoTool.cs`**

```csharp
using System.ComponentModel;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class FsInfoTool(HaFileSystem fs)
{
    [McpServerTool(Name = "fs_info")]
    [Description("Returns metadata for a Home Assistant virtual path: exists, isDirectory. Cheap existence check before read/exec.")]
    public async Task<CallToolResult> McpRun(string path, CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(await fs.InfoAsync(path, cancellationToken));
    }
}
```

- [ ] **Step 3: Write `FsReadTool.cs`**

```csharp
using System.ComponentModel;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class FsReadTool(HaFileSystem fs)
{
    [McpServerTool(Name = "fs_read")]
    [Description("Reads a Home Assistant virtual file: state.yaml returns the entity's live state + attributes; a *.sh file returns its usage (same as --help).")]
    public async Task<CallToolResult> McpRun(
        string path,
        int? offset = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(await fs.ReadAsync(path, offset, limit, cancellationToken));
    }
}
```

- [ ] **Step 4: Write `FsSearchTool.cs`**

```csharp
using System.ComponentModel;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class FsSearchTool(HaFileSystem fs)
{
    [McpServerTool(Name = "fs_search")]
    [Description("Searches across Home Assistant entity states (entity_id, friendly_name, attributes). Use to find e.g. everything currently 'on'.")]
    public async Task<CallToolResult> McpRun(
        string query,
        bool regex = false,
        string? path = null,
        string? directoryPath = null,
        string? filePattern = null,
        int maxResults = 50,
        int contextLines = 1,
        CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(
            await fs.SearchAsync(query, regex, path, directoryPath, filePattern, maxResults, contextLines, cancellationToken));
    }
}
```

- [ ] **Step 5: Write `FsExecTool.cs`**

```csharp
using System.ComponentModel;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class FsExecTool(HaFileSystem fs)
{
    [McpServerTool(Name = "fs_exec")]
    [Description("Runs a Home Assistant action file (a service call). path is the entity directory CWD (e.g. /ha/entities/light/kitchen); command is an action file invocation like 'turn_on.sh --brightness_pct 60'. Use '<service>.sh --help' to see arguments. This is NOT a shell — only *.sh action files run; anything else returns exit 127.")]
    public async Task<CallToolResult> McpRun(
        string path,
        string command,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(await fs.ExecAsync(path, command, timeoutSeconds, cancellationToken));
    }
}
```

- [ ] **Step 6: Compile**

Run: `dotnet build McpServerHomeAssistant/McpServerHomeAssistant.csproj`
Expected: builds (tools not yet registered — that's Task 14).

- [ ] **Step 7: Commit**

```bash
git add McpServerHomeAssistant/McpTools/FsGlobTool.cs McpServerHomeAssistant/McpTools/FsInfoTool.cs McpServerHomeAssistant/McpTools/FsReadTool.cs McpServerHomeAssistant/McpTools/FsSearchTool.cs McpServerHomeAssistant/McpTools/FsExecTool.cs
git commit -m "feat(ha-vfs): fs_* MCP tool wrappers"
```

---

### Task 13: `filesystem://ha` resource

**Files:**
- Create: `McpServerHomeAssistant/McpResources/FileSystemResource.cs`

- [ ] **Step 1: Write the file** (mirrors `McpServerSandbox/McpResources/FileSystemResource.cs`)

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpResources;

[McpServerResourceType]
public class FileSystemResource
{
    [McpServerResource(
        UriTemplate = "filesystem://ha",
        Name = "Home Assistant Filesystem",
        MimeType = "application/json")]
    [Description("Home Assistant entities and controls as a virtual filesystem")]
    public string GetHaInfo()
    {
        return JsonSerializer.Serialize(new
        {
            name = "ha",
            mountPoint = "/ha",
            description = "Home Assistant as a filesystem. Browse `/ha/entities/<class>/<id>/` or `/ha/areas/<room>/<entity_id>/`. `read state.yaml` for live state; `read <service>.sh` (or `exec '<service>.sh --help'`) for an action's arguments; `exec '<service>.sh --flag value'` to control a device. NOT a shell — exec only runs the listed *.sh action files (anything else returns exit 127). No create/edit/delete."
        });
    }
}
```

- [ ] **Step 2: Compile**

Run: `dotnet build McpServerHomeAssistant/McpServerHomeAssistant.csproj`
Expected: builds.

- [ ] **Step 3: Commit**

```bash
git add McpServerHomeAssistant/McpResources/FileSystemResource.cs
git commit -m "feat(ha-vfs): filesystem://ha mount resource"
```

---

### Task 14: DI wiring + remove `home_*` tools

**Files:**
- Modify: `McpServerHomeAssistant/Modules/ConfigModule.cs`
- Delete: `McpServerHomeAssistant/McpTools/McpHomeGetStateTool.cs`, `McpHomeListEntitiesTool.cs`, `McpHomeListServicesTool.cs`, `McpHomeCallServiceTool.cs`

- [ ] **Step 1: Delete the four `home_*` MCP tools**

```bash
git rm McpServerHomeAssistant/McpTools/McpHomeGetStateTool.cs \
       McpServerHomeAssistant/McpTools/McpHomeListEntitiesTool.cs \
       McpServerHomeAssistant/McpTools/McpHomeListServicesTool.cs \
       McpServerHomeAssistant/McpTools/McpHomeCallServiceTool.cs
```

- [ ] **Step 2: Rewrite `ConfigModule.cs`**

Replace the body of `ConfigureMcp` (the `services...` chain) with the version below. Note: `HomeAssistantSetupSummary` now takes `HaCatalogProvider` (changed in Task 16) — register the provider before it. Add `using Domain.Tools.HomeAssistant.Vfs;` and `using McpServerHomeAssistant.McpResources;`.

```csharp
            services
                .AddSingleton(settings)
                .AddHomeAssistantClient(settings.HomeAssistant.BaseUrl, settings.HomeAssistant.Token)
                .AddSingleton(sp => new HaCatalogProvider(sp.GetRequiredService<IHomeAssistantClient>))
                .AddSingleton(sp => new HaFileSystem(
                    sp.GetRequiredService<HaCatalogProvider>(),
                    sp.GetRequiredService<IHomeAssistantClient>))
                .AddSingleton(sp => new HomeAssistantSetupSummary(sp.GetRequiredService<HaCatalogProvider>()))
                .AddMcpServer()
                .WithHttpTransport()
                .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
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
                }))
                .WithTools<FsGlobTool>()
                .WithTools<FsInfoTool>()
                .WithTools<FsReadTool>()
                .WithTools<FsSearchTool>()
                .WithTools<FsExecTool>()
                .WithResources<FileSystemResource>()
                .WithPrompts<McpSystemPrompt>();

            return services;
```

> `sp.GetRequiredService<IHomeAssistantClient>` is a method group → `Func<IHomeAssistantClient>`, matching both constructors. `WithResources<T>` is the MCP SDK call used by `McpServerSandbox` to register a `[McpServerResourceType]`.

- [ ] **Step 3: Build the server**

Run: `dotnet build McpServerHomeAssistant/McpServerHomeAssistant.csproj`
Expected: FAIL — `HomeAssistantSetupSummary` still has the old `Func<IHomeAssistantClient>` constructor. This is fixed in Task 16; build will pass after Phase 5. For now, verify the only errors are about the `HomeAssistantSetupSummary` constructor.

- [ ] **Step 4: Commit**

```bash
git add McpServerHomeAssistant/Modules/ConfigModule.cs
git commit -m "feat(ha-vfs): register fs_* tools + resource; drop home_* tools"
```

---

## Phase 5 — Prompts, cleanup, journey test

### Task 15: End-to-end journey test (glob → read → help → exec)

**Files:**
- Create: `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemJourneyTests.cs`

Proves the realistic agent path through the engine in one test, against the fake client.

- [ ] **Step 1: Write the test**

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Files;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaFileSystemJourneyTests
{
    [Fact]
    public async Task Discover_Inspect_Help_Act()
    {
        var client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))) },
            Services = { Service("light", "turn_on", AnyEntityTarget(),
                ("brightness_pct", new HaServiceField { Selector = JsonNode.Parse("""{"number":{"min":1,"max":100}}""") })) },
            AreaTemplateJson = """{"areas":[{"id":"kitchen","name":"Kitchen","entities":["light.kitchen"]}]}"""
        };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);

        // 1. discover
        var classes = (JsonArray)await fs.GlobAsync("entities", "*", GlobMode.Directories, CancellationToken.None);
        classes.Select(n => n!.GetValue<string>()).ShouldContain("entities/light");

        // 2. inspect state
        var state = await fs.ReadAsync("entities/light/kitchen/state.yaml", null, null, CancellationToken.None);
        state["content"]!.GetValue<string>().ShouldContain("state: \"off\"");

        // 3. learn the action
        var help = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh --help", null, CancellationToken.None);
        help["stdout"]!.GetValue<string>().ShouldContain("--brightness_pct");

        // 4. act
        var act = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh --brightness_pct 60", null, CancellationToken.None);
        act["exitCode"]!.GetValue<int>().ShouldBe(0);
        client.LastCall!.Value.Data!["brightness_pct"]!.GetValue<int>().ShouldBe(60);

        // 5. area view resolves to the same entity
        var areaState = await fs.ReadAsync("areas/kitchen/light.kitchen/state.yaml", null, null, CancellationToken.None);
        areaState["content"]!.GetValue<string>().ShouldContain("entity_id: light.kitchen");
    }
}
```

- [ ] **Step 2: Run** — `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaFileSystemJourneyTests"`. Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemJourneyTests.cs
git commit -m "test(ha-vfs): end-to-end navigation + control journey"
```

---

### Task 16: Slim index prompt (`HomeAssistantSetupSummary`)

**Files:**
- Modify: `Domain/Prompts/HomeAssistantSetupSummary.cs`
- Modify: `Tests/Unit/Domain/HomeAssistant/HomeAssistantSetupSummaryTests.cs`

Repoint the summary at `HaCatalogProvider` and render a compact orientation index (mount root + areas with counts + class domains + total entity count) instead of the full per-entity dump.

- [ ] **Step 1: Replace the test file** (the old per-entity assertions no longer apply)

```csharp
using Domain.Prompts;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant;

public class HomeAssistantSetupSummaryTests
{
    private static HomeAssistantSetupSummary Build(FakeHaClient client) =>
        new(new HaCatalogProvider(() => client, new FakeTimeProvider()));

    [Fact]
    public async Task GetAsync_RendersMountAreasDomainsAndCounts()
    {
        var client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off"), Entity("sensor.salon_temp", "21") },
            AreaTemplateJson = """{"areas":[{"id":"salon","name":"Salón","entities":["sensor.salon_temp"]}]}"""
        };

        var text = await Build(client).GetAsync(CancellationToken.None);

        text.ShouldContain("/ha");
        text.ShouldContain("Salón");
        text.ShouldContain("light");
        text.ShouldContain("sensor");
        text.ShouldContain("2 entities");
    }

    [Fact]
    public async Task GetAsync_EmptyCatalog_ReturnsEmpty()
    {
        var client = new FakeHaClient();
        // No states -> provider treats as failure/empty -> summary empty.
        (await Build(client).GetAsync(CancellationToken.None)).ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify it fails** — `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistantSetupSummaryTests"`. Expected: FAIL (constructor signature changed; old impl).

- [ ] **Step 3: Replace `HomeAssistantSetupSummary.cs`**

```csharp
using System.Text;
using Domain.Tools.HomeAssistant.Vfs;

namespace Domain.Prompts;

// Builds the compact "Current setup" index appended to HomeAssistantPrompt at MCP-prompt-fetch
// time. It orients the agent (mount root, rooms, device classes, totals) without dumping every
// entity — details are pulled on demand through the /ha virtual filesystem. Backed by the shared
// HaCatalogProvider cache. Returns "" when the catalog is empty so the caller falls back to the
// static prompt alone.
public class HomeAssistantSetupSummary(HaCatalogProvider catalogProvider)
{
    public async Task<string> GetAsync(CancellationToken ct = default)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        if (catalog.Entities.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("## Current Home Assistant setup\n\n");
        sb.Append("Mounted at `/ha` — browse `/ha/entities/<class>/<id>/` or `/ha/areas/<room>/`.\n");
        sb.Append("Total: ").Append(catalog.Entities.Count).Append(" entities").Append('\n').Append('\n');

        sb.Append("### Rooms\n");
        foreach (var area in catalog.Areas.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            var count = catalog.EntityIdsInArea(area.Id).Count;
            if (count > 0)
            {
                sb.Append("- ").Append(area.Name).Append(" (`").Append(area.Id).Append("`): ")
                  .Append(count).Append(" entities\n");
            }
        }
        if (catalog.EntityIdsInArea(HaCatalog.UnassignedArea).Count is var u and > 0)
        {
            sb.Append("- (unassigned): ").Append(u).Append(" entities\n");
        }

        sb.Append("\n### Device classes\n");
        sb.Append(string.Join(", ", catalog.ClassDomains()));

        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run to verify it passes** — `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistantSetupSummaryTests"`. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Prompts/HomeAssistantSetupSummary.cs Tests/Unit/Domain/HomeAssistant/HomeAssistantSetupSummaryTests.cs
git commit -m "feat(ha-vfs): slim setup index backed by catalog provider"
```

---

### Task 17: Rewrite the workflow prompt

**Files:**
- Modify: `Domain/Prompts/HomeAssistantPrompt.cs`

- [ ] **Step 1: Replace `SystemPrompt`** (keep `Name` and `Description` constants unchanged) with the filesystem-idiom workflow:

```csharp
    public const string SystemPrompt =
        """
        ## Home Assistant Control (`/ha` filesystem)

        Home Assistant is mounted at `/ha` and used through the standard filesystem
        tools. The "## Current Home Assistant setup" index appended below lists the
        rooms, device classes, and counts — consult it first to orient.

        ### Layout

        - `/ha/entities/<class>/<id>/` — one directory per entity (e.g.
          `/ha/entities/light/kitchen/`). Contains `state.yaml` (live state +
          attributes) and one `<service>.sh` per available action.
        - `/ha/areas/<room>/<entity_id>/` — the same entities grouped by room.

        ### Workflow

        1. Find the entity: `glob_files` under `/ha/entities/<class>` or
           `/ha/areas/<room>`, or read the setup index.
        2. Inspect when you need an attribute as input: `text_read`
           `/ha/.../state.yaml`.
        3. Learn an action's arguments: `exec` `<service>.sh --help` (or
           `text_read` the `.sh` file — same content).
        4. Act: `exec` from the entity directory, e.g.
           `exec(path="/ha/entities/light/kitchen", command="turn_on.sh --brightness_pct 60")`.

        ### Reading results

        - `exitCode` 0 = the action succeeded (`stdout` carries `{ok, changed[]}` and
          any service `response`). It is authoritative — do NOT read `state.yaml`
          afterwards to confirm; HA propagates state asynchronously and the read is stale.
        - `exitCode` 2 = bad argument: re-run `--help` and rebuild; don't repeat the
          same shape.
        - `exitCode` 1 = HA rejected the call; `stderr` has the reason.
        - `exitCode` 127 = not a real action file. `/ha` is NOT a shell — only the
          listed `*.sh` files run. `stderr` lists the available actions.

        ### Notes

        - `state.yaml` reads are always live. Read one only when you need a specific
          attribute as INPUT to the next action (e.g. `source_list` before
          `select_source`).
        - Room targets: use the area `id` slug from the index, not the display name.
        - Climate: read the ambient (a room temperature sensor, or the climate entity's
          `current_temperature`) before choosing a direction; set heating targets above
          ambient and cooling targets below; change mode first if it conflicts.
        """;
```

- [ ] **Step 2: Build** — `dotnet build Domain/Domain.csproj`. Expected: builds.

- [ ] **Step 3: Commit**

```bash
git add Domain/Prompts/HomeAssistantPrompt.cs
git commit -m "feat(ha-vfs): rewrite workflow prompt for filesystem idiom"
```

---

### Task 18: Remove the obsolete `home_*` domain tools + tests; full build/test

**Files:**
- Delete: `Domain/Tools/HomeAssistant/HomeGetStateTool.cs`, `HomeListEntitiesTool.cs`, `HomeListServicesTool.cs`, `HomeCallServiceTool.cs`
- Delete: `Tests/Unit/Domain/HomeAssistant/HomeGetStateToolTests.cs`, `HomeListEntitiesToolTests.cs`, `HomeListServicesToolTests.cs`, `HomeCallServiceToolTests.cs`

- [ ] **Step 1: Delete the domain tools and their tests**

```bash
git rm Domain/Tools/HomeAssistant/HomeGetStateTool.cs \
       Domain/Tools/HomeAssistant/HomeListEntitiesTool.cs \
       Domain/Tools/HomeAssistant/HomeListServicesTool.cs \
       Domain/Tools/HomeAssistant/HomeCallServiceTool.cs \
       Tests/Unit/Domain/HomeAssistant/HomeGetStateToolTests.cs \
       Tests/Unit/Domain/HomeAssistant/HomeListEntitiesToolTests.cs \
       Tests/Unit/Domain/HomeAssistant/HomeListServicesToolTests.cs \
       Tests/Unit/Domain/HomeAssistant/HomeCallServiceToolTests.cs
```

- [ ] **Step 2: Search for any remaining references**

Run: `grep -rn "HomeGetStateTool\|HomeListEntitiesTool\|HomeListServicesTool\|HomeCallServiceTool" --include=*.cs . | grep -v obj`
Expected: no results. If any appear (e.g. another DI module or prompt registration), remove those references.

- [ ] **Step 3: Full solution build**

Run: `dotnet build`
Expected: PASS (this is where the Task 14 `ConfigModule` change finally compiles, now that `HomeAssistantSetupSummary` takes `HaCatalogProvider`).

- [ ] **Step 4: Full test run**

Run: `dotnet test Tests/Tests.csproj`
Expected: PASS — all VFS tests green; no references to deleted tools.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(ha): remove obsolete home_* domain tools and tests"
```

---

## Manual verification (after the plan)

Not automated (requires the live stack). The discovery/mount path is the existing, unchanged `McpFileSystemDiscovery` + `VirtualFileSystemRegistry`, so a smoke test is enough:

1. Launch with the Linux override (see `CLAUDE.md` → Launching), including `mcp-homeassistant`.
2. In WebChat, ask the agent to `glob_files /ha/entities` → expect class-domain directories.
3. Ask it to read `/ha/entities/<class>/<id>/state.yaml` → expect live YAML.
4. Ask it to turn something on → expect an `exec` of `<service>.sh` with `exitCode: 0`.
5. Confirm the four `home_*` tools no longer appear in the agent's tool list and `/ha` shows in the mount list.

---

## Self-Review

**Spec coverage:**
- Namespace (entities/ + areas/ view) → Tasks 3, 5, 10, 15. ✓
- `.sh` model + exec + `--help` + 127 guard → Tasks 4, 7, 8, 11. ✓
- GNU-flag args (scalars/lists/objects) → Task 8. ✓
- Read surface (state.yaml fresh, cat .sh, search) → Task 10. ✓
- Slim index + removed home_* + rewritten prompt → Tasks 14, 16, 17, 18. ✓
- Backend components (path/resolver/help/args/state/area/engine + wrappers + resource) → Tasks 2–13. ✓
- Caching (catalog cached, state fresh) → Tasks 9, 10. ✓
- Error handling (not-found, unsupported via absent tools, bad-arg, 127) → Tasks 10, 11; create/edit/delete are simply not implemented so the registry returns `unsupported_operation` automatically. ✓

**Type consistency:** `HaCatalog`, `HaAreaEntities`, `HaVfsNode`/`HaVfsKind`, `HaActionResolver.ServicesFor`, `HaServiceHelpRenderer.Render`, `HaArgParser.Parse`, `HaStateRenderer.ToYaml`, `HaCatalogProvider.GetAsync`, `HaFileSystem.{Glob,Info,Read,Search,Exec}Async`, `GlobMode` (reused from `Domain.Tools.Files`) — names used consistently across tasks. `HaFileSystem` is `partial` (Tasks 10 + 11).

**Known sequencing note:** Task 14 leaves the server temporarily non-compiling (depends on the Task 16 `HomeAssistantSetupSummary` constructor change); the full build is green by Task 18. Each Domain/Test task (1–11, 15, 16) compiles and passes independently. If executing strictly task-by-task with a build gate, run Tasks 14, 16, 17, 18 as one group before the final full build.
