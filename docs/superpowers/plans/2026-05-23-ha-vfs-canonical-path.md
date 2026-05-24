# HA VFS Single Canonical Path Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Home Assistant VFS entity paths resolve strictly — only the exact canonical directory name a listing shows works — so `read`/`exec`/`info`/`search` agree with `glob`, and near-misses return a "did you mean" hint instead of silently resolving.

**Architecture:** `HaVfsPath.Parse` becomes purely structural (returns the raw entity *segment*, no `StripNice`-reconstructed id). A new catalog-aware `HaFileSystem.ResolveEntity` maps a segment to an entity only when it equals `HaSlug.Compose(id, friendlyName)`, and emits the correct name as a hint on a recognizable-but-non-canonical segment. All path-taking consumers route through it. `glob` is untouched (already strict).

**Tech Stack:** .NET 10, C#, xUnit, Shouldly, `Microsoft.Extensions.Time.Testing` (`FakeTimeProvider`).

**Spec:** `docs/superpowers/specs/2026-05-23-ha-vfs-canonical-path-design.md`

---

## File Structure

**Production (all under `Domain/Tools/HomeAssistant/Vfs/`):**
- `HaVfsPath.cs` — `HaVfsNode` gains `EntitySegment` and drops `EntityId`; `Parse`/`Leaf` become structural (no `StripNice`).
- `HaFileSystem.cs` — new `EntityResolution` struct + `ResolveEntity` helper; `NotFound` gains an optional hint; `ReadAsync`, a new sync `ReadAction`, `Resolve`, and `ScopeEntities` route through `ResolveEntity`. `ReadStateAsync` keeps its signature.
- `HaFileSystem.Exec.cs` — the entity-dir existence check routes through `ResolveEntity` and emits a hint on a `127`.
- `Domain/Prompts/HomeAssistantPrompt.cs` — replace the "bare id… both resolve" line with the single-canonical-name rule.

**Tests (all under `Tests/Unit/Domain/HomeAssistant/Vfs/`):**
- `HaFileSystemReadTests.cs` — add strict-behavior tests; migrate three bare-id paths to composite.
- `HaFileSystemJourneyTests.cs` — migrate to composite paths; add a near-miss/hint step.
- `HaVfsPathTests.cs` — assert `EntitySegment` (+ `ClassDomain`/`Area`) instead of `EntityId`.

**Unaffected (do not touch):** `HaTreeTests.cs` (glob, fixtures have no friendly names), `HaFileSystemTimeoutTests.cs` (search with null scope), `HaSlug.cs`, `HaTree.cs`, the MCP `fs_*` wrappers, DI/wiring, the slim index prompt.

---

## Task 1: Strict canonical entity resolution

This is one atomic refactor: the `Parse` contract change ripples to every consumer, so the test project will not compile between the production edits (Steps 3–6) and the test edits (Steps 7–9). Verification happens at the RED boundary (Step 2, before any change) and the GREEN boundary (Step 10, after all changes), with a production-only build checkpoint after Step 6.

**Files:**
- Modify: `Domain/Tools/HomeAssistant/Vfs/HaVfsPath.cs`
- Modify: `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs`
- Modify: `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.Exec.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemJourneyTests.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaVfsPathTests.cs`

- [ ] **Step 1: Write the failing strict-behavior tests**

Append these to `HaFileSystemReadTests.cs`, inside the `HaFileSystemReadTests` class (after the last test, before the closing brace). They reuse the existing private `Build` helper, whose entity `light.kitchen` has friendly name `"Kitchen"` (so its canonical directory is `kitchen_(kitchen)`).

```csharp
    [Fact]
    public async Task ReadAsync_BareId_WhenFriendlyNameExists_NotFoundWithHint()
    {
        var fs = Build(out _);
        var read = await fs.ReadAsync("entities/light/kitchen/state.json", null, null, CancellationToken.None);
        read["ok"]!.GetValue<bool>().ShouldBeFalse();
        read["errorCode"]!.GetValue<string>().ShouldBe("not_found");
        read["hint"]!.GetValue<string>().ShouldContain("kitchen_(kitchen)");
    }

    [Fact]
    public async Task ReadAsync_WrongSuffix_NotFoundWithHint()
    {
        var fs = Build(out _);
        var read = await fs.ReadAsync("entities/light/kitchen_(wrong)/state.json", null, null, CancellationToken.None);
        read["ok"]!.GetValue<bool>().ShouldBeFalse();
        read["hint"]!.GetValue<string>().ShouldContain("kitchen_(kitchen)");
    }

    [Fact]
    public async Task ReadAsync_CompositeName_Resolves()
    {
        var fs = Build(out _);
        var read = await fs.ReadAsync("entities/light/kitchen_(kitchen)/state.json", null, null, CancellationToken.None);
        read["content"]!.GetValue<string>().ShouldContain("\"entity_id\": \"light.kitchen\"");
    }

    [Fact]
    public async Task ExecAsync_BareId_WhenFriendlyNameExists_127WithHint()
    {
        var fs = Build(out _);
        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh", null, CancellationToken.None);
        result["exitCode"]!.GetValue<int>().ShouldBe(127);
        result["stderr"]!.GetValue<string>().ShouldContain("kitchen_(kitchen)");
    }

    [Fact]
    public async Task ReadAsync_EntityWithoutFriendlyName_ResolvesByBareId()
    {
        var client = new FakeHaClient { States = { Entity("light.porch", "off") } };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);
        var read = await fs.ReadAsync("entities/light/porch/state.json", null, null, CancellationToken.None);
        read["content"]!.GetValue<string>().ShouldContain("\"entity_id\": \"light.porch\"");
    }
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaFileSystemReadTests" 2>&1 | tail -20`
Expected: the four strict cases FAIL (current lenient code resolves the bare id and wrong suffix, so `ok` is `true`/content is returned and the `not_found`/`127`/hint assertions fail). `ReadAsync_EntityWithoutFriendlyName_ResolvesByBareId` already PASSES (porch has no friendly name, so bare == canonical). Confirm the failures are assertion failures, not compile errors.

- [ ] **Step 3: Make `HaVfsPath` structural**

Replace the entire contents of `Domain/Tools/HomeAssistant/Vfs/HaVfsPath.cs` with:

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
    string? EntitySegment = null,
    string? Service = null);

public static class HaVfsPath
{
    public const string StateFileName = "state.json";

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
        3 => new HaVfsNode(HaVfsKind.EntityDir, ClassDomain: s[1], EntitySegment: s[2]),
        4 => Leaf(s[3], classDomain: s[1], area: null, segment: s[2]),
        _ => new HaVfsNode(HaVfsKind.Unknown)
    };

    private static HaVfsNode ParseAreas(string[] s) => s.Length switch
    {
        1 => new HaVfsNode(HaVfsKind.AreasRoot),
        2 => new HaVfsNode(HaVfsKind.AreaDir, Area: s[1]),
        3 => new HaVfsNode(HaVfsKind.EntityDir, Area: s[1], EntitySegment: s[2]),
        4 => Leaf(s[3], classDomain: null, area: s[1], segment: s[2]),
        _ => new HaVfsNode(HaVfsKind.Unknown)
    };

    private static HaVfsNode Leaf(string fileName, string? classDomain, string? area, string segment)
    {
        if (fileName.Equals(StateFileName, StringComparison.Ordinal))
        {
            return new HaVfsNode(HaVfsKind.StateFile, ClassDomain: classDomain, Area: area, EntitySegment: segment);
        }
        if (fileName.EndsWith(".sh", StringComparison.Ordinal))
        {
            return new HaVfsNode(HaVfsKind.ActionFile, ClassDomain: classDomain, Area: area, EntitySegment: segment, Service: fileName[..^3]);
        }
        return new HaVfsNode(HaVfsKind.Unknown);
    }
}
```

- [ ] **Step 4: Add `EntityResolution` + `ResolveEntity` + a hinted `NotFound` to `HaFileSystem.cs`**

In `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs`, replace the existing `Resolve` method (the `private static (bool Exists, bool IsDir) Resolve(...)` block) with the resolver, the strict `Resolve`, and the helper struct:

```csharp
    private readonly record struct EntityResolution(HaEntityState? Entity, string? Hint);

    // Strict canonical resolution: a segment resolves only if it equals the entity's composed
    // directory name (HaSlug.Compose). A recognizable object-id with a non-canonical segment yields
    // a hint naming the correct directory; an unknown object-id yields no hint. Keeps read/exec/info/
    // search in lockstep with glob, which composes the same names.
    private static EntityResolution ResolveEntity(HaCatalog catalog, HaVfsNode node)
    {
        var segment = node.EntitySegment!;
        var candidateId = node.Area is not null
            ? HaSlug.StripNice(segment)
            : $"{node.ClassDomain}.{HaSlug.StripNice(segment)}";

        var entity = catalog.EntityById(candidateId);
        if (entity is null)
        {
            return new EntityResolution(null, null);
        }

        var canonical = node.Area is not null
            ? HaSlug.Compose(entity.EntityId, HaCatalog.FriendlyName(entity))
            : HaSlug.Compose(HaCatalog.ObjectOf(entity.EntityId), HaCatalog.FriendlyName(entity));

        return segment == canonical
            ? new EntityResolution(entity, null)
            : new EntityResolution(null, canonical);
    }

    private static (bool Exists, bool IsDir) Resolve(HaVfsNode node, HaCatalog catalog) => node.Kind switch
    {
        HaVfsKind.Root or HaVfsKind.EntitiesRoot or HaVfsKind.AreasRoot => (true, true),
        HaVfsKind.ClassDir => (catalog.ClassDomains().Contains(node.ClassDomain), true),
        HaVfsKind.AreaDir => (catalog.AreaSlugs().Contains(node.Area), true),
        HaVfsKind.EntityDir => (ResolveEntity(catalog, node).Entity is not null, true),
        HaVfsKind.StateFile => (ResolveEntity(catalog, node).Entity is not null, false),
        HaVfsKind.ActionFile => ResolveEntity(catalog, node).Entity is { } e
            && HaActionResolver.ServicesFor(e.EntityId, catalog.Services).Any(s => s.Service == node.Service)
            ? (true, false)
            : (false, false),
        _ => (false, false)
    };
```

Then replace the existing `NotFound` method:

```csharp
    private static JsonObject NotFound(string path) =>
        Domain.Tools.ToolError.Create(Domain.Tools.ToolError.Codes.NotFound, $"No such path: {path}");
```

with the hinted overload:

```csharp
    private static JsonObject NotFound(string path, string? canonicalName = null) =>
        Domain.Tools.ToolError.Create(
            Domain.Tools.ToolError.Codes.NotFound,
            $"No such path: {path}",
            hint: canonicalName is null
                ? null
                : $"Use the exact directory name a listing returns: '{canonicalName}'.");
```

- [ ] **Step 5: Route `ReadAsync`, `ReadAction`, and `ScopeEntities` through `ResolveEntity` in `HaFileSystem.cs`**

Replace `ReadAsync` (the `public async Task<JsonNode> ReadAsync(...)` block) with:

```csharp
    public async Task<JsonNode> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        var node = HaVfsPath.Parse(path);
        if (node.Kind is not (HaVfsKind.StateFile or HaVfsKind.ActionFile))
        {
            return NotFound(path);
        }

        var catalog = await catalogProvider.GetAsync(ct);
        var resolution = ResolveEntity(catalog, node);
        if (resolution.Entity is null)
        {
            return NotFound(path, resolution.Hint);
        }

        var entityId = resolution.Entity.EntityId;
        return node.Kind == HaVfsKind.StateFile
            ? await ReadStateAsync(path, entityId, offset, limit, ct)
            : ReadAction(path, entityId, node.Service!, catalog);
    }
```

Replace the existing `ReadActionAsync` method (the `private async Task<JsonNode> ReadActionAsync(string path, HaVfsNode node, CancellationToken ct)` block) with this sync helper that takes the already-resolved id and catalog:

```csharp
    private static JsonNode ReadAction(string path, string entityId, string service, HaCatalog catalog)
    {
        var svc = HaActionResolver.ServicesFor(entityId, catalog.Services)
            .FirstOrDefault(s => s.Service.Equals(service, StringComparison.Ordinal));
        return svc is null
            ? NotFound(path)
            : BuildReadResult(path, HaServiceHelpRenderer.Render(entityId, svc), null, null);
    }
```

(Leave `ReadStateAsync(string path, string entityId, int? offset, int? limit, CancellationToken ct)` unchanged — `ReadAsync` now passes it the resolved id.)

In `ScopeEntities`, replace the `EntityDir`/`StateFile` switch arm:

```csharp
            HaVfsKind.EntityDir or HaVfsKind.StateFile =>
                catalog.EntityById(node.EntityId!) is { } entity ? [entity] : [],
```

with:

```csharp
            HaVfsKind.EntityDir or HaVfsKind.StateFile =>
                ResolveEntity(catalog, node).Entity is { } entity ? [entity] : [],
```

- [ ] **Step 6: Route the exec entity-dir check through `ResolveEntity` in `HaFileSystem.Exec.cs`**

In `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.Exec.cs`, replace this block:

```csharp
        var catalog = await catalogProvider.GetAsync(ct);
        var node = HaVfsPath.Parse(path);
        if (node.Kind != HaVfsKind.EntityDir || catalog.EntityById(node.EntityId!) is null)
        {
            return Done(127, "", $"Not an entity directory: {path}. cd into /ha/entities/<class>/<id> first.");
        }

        var tokens = ShellTokenize(command);
        var entityId = node.EntityId!;
```

with:

```csharp
        var catalog = await catalogProvider.GetAsync(ct);
        var node = HaVfsPath.Parse(path);
        if (node.Kind != HaVfsKind.EntityDir)
        {
            return Done(127, "", $"Not an entity directory: {path}. cd into /ha/entities/<class>/<id> first.");
        }

        var resolution = ResolveEntity(catalog, node);
        if (resolution.Entity is null)
        {
            var didYouMean = resolution.Hint is null
                ? ""
                : $" Did you mean '{resolution.Hint}'? Copy the exact name a listing returns.";
            return Done(127, "", $"No such entity directory: {path}.{didYouMean}");
        }

        var tokens = ShellTokenize(command);
        var entityId = resolution.Entity.EntityId;
```

- [ ] **Step 7: Verify production code compiles**

Run: `dotnet build Domain/Domain.csproj 2>&1 | tail -5`
Expected: `Build succeeded. 0 Error(s)`. (The test project will not compile yet — that is fixed in Steps 8–10.)

- [ ] **Step 8: Update `HaVfsPathTests.cs` to the structural contract**

Entity nodes no longer carry `EntityId`; assert `EntitySegment` plus `ClassDomain`/`Area`, and the composite cases now keep the raw segment. Replace the four entity/leaf `[Fact]`s and the three composite `[Fact]`s as follows.

`Parse_EntityDir_FromEntitiesRoot`:

```csharp
    [Fact]
    public void Parse_EntityDir_FromEntitiesRoot()
    {
        var n = HaVfsPath.Parse("entities/light/kitchen");
        n.Kind.ShouldBe(HaVfsKind.EntityDir);
        n.ClassDomain.ShouldBe("light");
        n.EntitySegment.ShouldBe("kitchen");
    }
```

`Parse_EntityDir_FromAreasRoot_UsesFullEntityId`:

```csharp
    [Fact]
    public void Parse_EntityDir_FromAreasRoot_UsesFullEntityId()
    {
        var n = HaVfsPath.Parse("areas/salon/light.salon");
        n.Kind.ShouldBe(HaVfsKind.EntityDir);
        n.Area.ShouldBe("salon");
        n.EntitySegment.ShouldBe("light.salon");
    }
```

`Parse_StateFile`:

```csharp
    [Fact]
    public void Parse_StateFile()
    {
        var n = HaVfsPath.Parse("entities/light/kitchen/state.json");
        n.Kind.ShouldBe(HaVfsKind.StateFile);
        n.ClassDomain.ShouldBe("light");
        n.EntitySegment.ShouldBe("kitchen");
    }
```

`Parse_ActionFile_StripsShExtension`:

```csharp
    [Fact]
    public void Parse_ActionFile_StripsShExtension()
    {
        var n = HaVfsPath.Parse("entities/light/kitchen/turn_on.sh");
        n.Kind.ShouldBe(HaVfsKind.ActionFile);
        n.ClassDomain.ShouldBe("light");
        n.EntitySegment.ShouldBe("kitchen");
        n.Service.ShouldBe("turn_on");
    }
```

`Parse_ActionFile_UnderArea`:

```csharp
    [Fact]
    public void Parse_ActionFile_UnderArea()
    {
        var n = HaVfsPath.Parse("areas/salon/light.salon/toggle.sh");
        n.Kind.ShouldBe(HaVfsKind.ActionFile);
        n.Area.ShouldBe("salon");
        n.EntitySegment.ShouldBe("light.salon");
        n.Service.ShouldBe("toggle");
    }
```

`Parse_CompositeEntityDir_StripsNiceName` → rename and assert the raw segment is preserved:

```csharp
    [Fact]
    public void Parse_CompositeEntityDir_KeepsRawSegment()
    {
        var n = HaVfsPath.Parse("entities/climate/0x00158d00abcd_(aire-acondicionado-salon)");
        n.Kind.ShouldBe(HaVfsKind.EntityDir);
        n.ClassDomain.ShouldBe("climate");
        n.EntitySegment.ShouldBe("0x00158d00abcd_(aire-acondicionado-salon)");
    }
```

`Parse_CompositeStateFile_StripsNiceName` → rename:

```csharp
    [Fact]
    public void Parse_CompositeStateFile_KeepsRawSegment()
    {
        var n = HaVfsPath.Parse("entities/climate/0x00158d00abcd_(aire-acondicionado-salon)/state.json");
        n.Kind.ShouldBe(HaVfsKind.StateFile);
        n.ClassDomain.ShouldBe("climate");
        n.EntitySegment.ShouldBe("0x00158d00abcd_(aire-acondicionado-salon)");
    }
```

`Parse_CompositeActionFile_UnderArea_StripsNiceName` → rename:

```csharp
    [Fact]
    public void Parse_CompositeActionFile_UnderArea_KeepsRawSegment()
    {
        var n = HaVfsPath.Parse("areas/salon/climate.0x00158d00abcd_(aire-acondicionado-salon)/turn_off.sh");
        n.Kind.ShouldBe(HaVfsKind.ActionFile);
        n.Area.ShouldBe("salon");
        n.EntitySegment.ShouldBe("climate.0x00158d00abcd_(aire-acondicionado-salon)");
        n.Service.ShouldBe("turn_off");
    }
```

(Leave `Parse_Roots`, `Parse_ClassDir`, `Parse_AreaDir`, and `Parse_Unknown` unchanged.)

- [ ] **Step 9: Migrate the bare-id paths in `HaFileSystemReadTests.cs` and `HaFileSystemJourneyTests.cs`**

In `HaFileSystemReadTests.cs`, the `Build` fixture entity `light.kitchen` has friendly name `"Kitchen"`, so its canonical directory is `kitchen_(kitchen)`. Update the three tests that use the bare path:

- `InfoAsync_EntityDir_Exists`: change `"entities/light/kitchen"` to `"entities/light/kitchen_(kitchen)"`.
- `ReadAsync_StateFile_RendersFreshJson`: change `"entities/light/kitchen/state.json"` to `"entities/light/kitchen_(kitchen)/state.json"`.
- `ReadAsync_ActionFile_RendersHelp`: change `"entities/light/kitchen/turn_on.sh"` to `"entities/light/kitchen_(kitchen)/turn_on.sh"`.

(The `ghost`, glob, search, two-AC, and `ExecAsync_ResolvesViaCompositePath` tests already use missing entities or composite paths — leave them.)

In `HaFileSystemJourneyTests.cs`, replace the body steps (the lines after `var fs = new HaFileSystem(...)` through the end of the method) with:

```csharp
        // 1. discover
        var globResult = await fs.GlobAsync("entities", "*", GlobMode.Directories, CancellationToken.None);
        globResult["entries"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldContain("entities/light");
        FsResultContract.TryValidate("fs_glob", globResult, out var err).ShouldBeTrue(err);

        // 2. inspect state (the exact directory name a listing returns)
        var state = await fs.ReadAsync("entities/light/kitchen_(kitchen)/state.json", null, null, CancellationToken.None);
        state["content"]!.GetValue<string>().ShouldContain("\"state\": \"off\"");

        // 3. learn the action
        var help = await fs.ExecAsync("entities/light/kitchen_(kitchen)", "turn_on.sh --help", null, CancellationToken.None);
        help["stdout"]!.GetValue<string>().ShouldContain("--brightness_pct");

        // 4. act
        var act = await fs.ExecAsync("entities/light/kitchen_(kitchen)", "turn_on.sh --brightness_pct 60", null, CancellationToken.None);
        act["exitCode"]!.GetValue<int>().ShouldBe(0);
        client.LastCall!.Value.Data!["brightness_pct"]!.GetValue<int>().ShouldBe(60);

        // 4b. a bare id (when a friendly name exists) is rejected with a hint
        var nearMiss = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh", null, CancellationToken.None);
        nearMiss["exitCode"]!.GetValue<int>().ShouldBe(127);
        nearMiss["stderr"]!.GetValue<string>().ShouldContain("kitchen_(kitchen)");

        // 5. area view resolves to the same entity via its canonical name
        var areaState = await fs.ReadAsync("areas/kitchen/light.kitchen_(kitchen)/state.json", null, null, CancellationToken.None);
        areaState["content"]!.GetValue<string>().ShouldContain("\"entity_id\": \"light.kitchen\"");
```

- [ ] **Step 10: Run the full HA suite to verify GREEN**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistant" 2>&1 | tail -6`
Expected: `Passed! - Failed: 0`. All HA tests (new strict cases, migrated read/journey/path tests, untouched glob/tree/timeout tests) pass with no warnings.

- [ ] **Step 11: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaVfsPath.cs Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs Domain/Tools/HomeAssistant/Vfs/HaFileSystem.Exec.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaVfsPathTests.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemJourneyTests.cs
git commit -m "feat(ha-vfs): strict single-canonical entity path resolution"
```

---

## Task 2: Update the HA prompt

**Files:**
- Modify: `Domain/Prompts/HomeAssistantPrompt.cs`

- [ ] **Step 1: Replace the "both resolve" guidance**

In the `### Layout` section, replace this sentence (the tail of the entity-directory-name bullet):

```
          `glob_files` alone identifies a device — pick by the name. You may address a path by
          the full segment or by the bare id; both resolve.
```

with:

```
          `glob_files` alone identifies a device — pick by the name. Use that exact directory
          name verbatim in later calls; a bare id or a guessed `_(...)` suffix will NOT resolve
          (a near-miss returns a "did you mean" hint with the correct name).
```

- [ ] **Step 2: Verify Domain still builds**

Run: `dotnet build Domain/Domain.csproj 2>&1 | tail -3`
Expected: `Build succeeded. 0 Error(s)`. (Prompt is a `const string`; this just guards against a broken raw-string literal.)

- [ ] **Step 3: Confirm no trailing newline (project convention: no trailing newline in any `.cs`)**

Run: `tail -c1 Domain/Prompts/HomeAssistantPrompt.cs | xxd | grep -q '7d' && echo OK-no-newline || echo FIX-trailing-newline`
Expected: `OK-no-newline` (file ends with `}`, byte `0x7d`). If it prints `FIX-trailing-newline`, run `perl -i -0pe 's/\n\z//' Domain/Prompts/HomeAssistantPrompt.cs`.

- [ ] **Step 4: Commit**

```bash
git add Domain/Prompts/HomeAssistantPrompt.cs
git commit -m "docs(ha): single canonical path rule, drop bare-id leniency"
```

---

## Task 3: Full verification

**Files:** none (verification only)

- [ ] **Step 1: Run the whole non-E2E unit suite for regressions**

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E" 2>&1 | tail -8`
Expected: no *new* failures versus baseline. Per the project baseline, ~148 `Category!=E2E` failures are pre-existing `DockerUnavailableException` in this WSL environment and are NOT regressions; every `HomeAssistant`-namespaced test must pass. If unsure, compare against `git stash` of the working tree, or re-run `--filter "FullyQualifiedName~HomeAssistant"` and confirm 0 failures there.

- [ ] **Step 2: Mark the spec implemented**

Append to `docs/superpowers/specs/2026-05-23-ha-vfs-canonical-path-design.md`:

```markdown

## Status

Implemented 2026-05-23 — see plan `docs/superpowers/plans/2026-05-23-ha-vfs-canonical-path.md`.
```

```bash
git add docs/superpowers/specs/2026-05-23-ha-vfs-canonical-path-design.md
git commit -m "docs(ha-vfs): mark canonical-path spec implemented"
```

---

## Self-Review

**Spec coverage:**
- Strict exact-name resolution → Task 1 Steps 3–6 (`ResolveEntity`, all consumers rewired).
- "Did you mean" hint on near-miss → Task 1 Steps 4 (`NotFound` hint), 6 (exec hint); tested Step 1.
- No-friendly-name entity resolves as bare `id` → tested Step 1 (`ReadAsync_EntityWithoutFriendlyName_ResolvesByBareId`).
- `Parse` becomes purely structural → Task 1 Step 3; structural tests Step 8.
- glob unchanged / consistent by construction → `HaTree` untouched; both compose via `HaSlug.Compose`.
- Prompt single-rule update → Task 2.
- HA-only scope → only `Domain/Tools/HomeAssistant/Vfs/*` and the HA prompt change.
- Info near-miss returns `exists:false` with no hint (FsInfoResult has no hint field) — a deliberate, noted narrowing of the spec's "read/info" hint wording; read/exec carry the hint that drives self-correction.

**Placeholder scan:** none — every step has concrete code/commands and expected output.

**Type consistency:** `EntityResolution(HaEntityState? Entity, string? Hint)`, `ResolveEntity(HaCatalog, HaVfsNode)`, `NotFound(string, string?)`, `ReadAction(string, string, string, HaCatalog)`, `HaVfsNode(... EntitySegment ...)`, and `HaSlug.Compose`/`StripNice`/`HaCatalog.ObjectOf`/`HaCatalog.FriendlyName` are used identically across Task 1 Steps 3–6 and the tests in Steps 1, 8, 9.
