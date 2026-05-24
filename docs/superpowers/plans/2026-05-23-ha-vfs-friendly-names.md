# HA VFS Human-Readable Directory Names — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `glob` over the Home Assistant VFS self-identifying by naming each entity directory `<id>_(<name-slug>)` (id + slugified `friendly_name`) in both `entities/` and `areas/`, while resolution recovers the id from the segment so the agent can pick the right device from `glob` alone.

**Architecture:** A new pure `HaSlug` helper owns the format (`Compose`/`StripNice`/`Slugify`). `HaTree` emits composite segments using each entity's `friendly_name`; `HaVfsPath.Parse` strips the `_(...)` suffix to recover the id and stays pure/structural. Resolution keys on the id prefix (entity ids never contain `(`), so a bare id still resolves and adversarial names can't corrupt parsing. `HaFileSystem` logic is unchanged.

**Tech Stack:** .NET 10, C#, `System.Text.Json.Nodes`, `System.Globalization` (Unicode normalize for diacritic folding), xUnit + Shouldly.

## Spec

`docs/superpowers/specs/2026-05-23-ha-vfs-friendly-names-design.md`

## Conventions (read once)

- **No trailing newline** in any `.cs` file (source AND test) — match sibling files.
- File-scoped namespaces, no XML doc comments, LINQ over loops, records for DTOs.
- Pure logic in `Domain/Tools/HomeAssistant/Vfs/` (depends only on `Domain.Contracts`).
- Run a filtered test: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~<ClassName>"`.

## File Structure

**Create:**
- `Domain/Tools/HomeAssistant/Vfs/HaSlug.cs` — `Compose(id, friendlyName)`, `StripNice(segment)`, `Slugify(name)`.
- `Tests/Unit/Domain/HomeAssistant/Vfs/HaSlugTests.cs`.

**Modify:**
- `Domain/Tools/HomeAssistant/Vfs/HaCatalog.cs` — add static `FriendlyName(HaEntityState?)`.
- `Domain/Tools/HomeAssistant/Vfs/HaVfsPath.cs` — strip the `_(...)` suffix off the entity-dir segment.
- `Domain/Tools/HomeAssistant/Vfs/HaTree.cs` — emit composite entity-dir segments.
- `Domain/Prompts/HomeAssistantPrompt.cs` — one line documenting the `<id>_(<name>)` naming (Task 6).
- Tests: `HaCatalogTests.cs`, `HaVfsPathTests.cs`, `HaTreeTests.cs`, `HaFileSystemReadTests.cs`, `HaFileSystemJourneyTests.cs`.

---

## Task 1: `HaSlug` — format helper

**Files:**
- Create: `Domain/Tools/HomeAssistant/Vfs/HaSlug.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaSlugTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaSlugTests
{
    [Theory]
    [InlineData("Kitchen", "kitchen")]
    [InlineData("Aire Acondicionado Salón", "aire-acondicionado-salon")]
    [InlineData("Salón (1/2)", "salon-1-2")]
    [InlineData("  Multiple   Spaces  ", "multiple-spaces")]
    [InlineData("Año_Niño", "ano-nino")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("!!!", "")]
    public void Slugify_ProducesSafeSlug(string input, string expected) =>
        HaSlug.Slugify(input).ShouldBe(expected);

    [Fact]
    public void Slugify_CapsLengthAtWordBoundary()
    {
        var slug = HaSlug.Slugify(string.Join(' ', Enumerable.Repeat("word", 40)));
        slug.Length.ShouldBeLessThanOrEqualTo(60);
        slug.ShouldNotEndWith("-");
    }

    [Fact]
    public void Compose_AppendsNiceName()
    {
        HaSlug.Compose("climate.0x00158d00abcd", "Aire Acondicionado Salón")
            .ShouldBe("climate.0x00158d00abcd_(aire-acondicionado-salon)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void Compose_NoUsableName_ReturnsBareId(string? name) =>
        HaSlug.Compose("light.kitchen", name).ShouldBe("light.kitchen");

    [Theory]
    [InlineData("climate.0x00158d00abcd_(aire-acondicionado-salon)", "climate.0x00158d00abcd")]
    [InlineData("ac_salon_(aire-acondicionado-salon)", "ac_salon")]
    [InlineData("light.kitchen", "light.kitchen")]
    public void StripNice_RecoversId(string segment, string expected) =>
        HaSlug.StripNice(segment).ShouldBe(expected);

    [Fact]
    public void StripNice_AdversarialSuffix_StillRecoversId()
    {
        // The id has no '(', so the first "_(" always delimits; anything after is decorative.
        HaSlug.StripNice("climate.ac_(a)_(b)").ShouldBe("climate.ac");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaSlugTests"`
Expected: FAIL — `HaSlug` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Globalization;

namespace Domain.Tools.HomeAssistant.Vfs;

// Renders the human-readable directory segment `<id>_(<name-slug>)` and recovers the id from it.
// An HA entity_id / object_id (charset [a-z0-9_.]) never contains '(', so the first "_(" is always
// the delimiter — StripNice keys on it and ignores the decorative suffix, making the round-trip safe
// even for adversarial friendly names. Slugifying the name also guarantees it cannot contain the
// delimiter characters in the first place.
public static class HaSlug
{
    private const int MaxLength = 60;
    private const string Delimiter = "_(";

    public static string Compose(string id, string? friendlyName)
    {
        var slug = Slugify(friendlyName);
        return slug.Length == 0 ? id : $"{id}{Delimiter}{slug})";
    }

    public static string StripNice(string segment)
    {
        var i = segment.IndexOf(Delimiter, StringComparison.Ordinal);
        return i < 0 ? segment : segment[..i];
    }

    public static string Slugify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var chars = name.Normalize(NormalizationForm.FormD)
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .Select(ch => char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ')
            .ToArray();
        var slug = string.Join('-', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length <= MaxLength ? slug : TrimToWord(slug);
    }

    private static string TrimToWord(string slug)
    {
        var cut = slug[..MaxLength];
        var lastDash = cut.LastIndexOf('-');
        return lastDash > 0 ? cut[..lastDash] : cut;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaSlugTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaSlug.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaSlugTests.cs
git commit -m "feat(ha-vfs): HaSlug composite name format helper"
```

---

## Task 2: `HaCatalog.FriendlyName`

**Files:**
- Modify: `Domain/Tools/HomeAssistant/Vfs/HaCatalog.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaCatalogTests.cs`

- [ ] **Step 1: Add the failing test** (append this method inside `HaCatalogTests`, and add `using System.Text.Json.Nodes;` to the top of the file alongside the existing usings)

```csharp
    [Fact]
    public void FriendlyName_ReadsAttribute_OrNull()
    {
        var withName = Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen")));
        var without = Entity("light.hall", "off");
        HaCatalog.FriendlyName(withName).ShouldBe("Kitchen");
        HaCatalog.FriendlyName(without).ShouldBeNull();
        HaCatalog.FriendlyName(null).ShouldBeNull();
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaCatalogTests"`
Expected: FAIL — `HaCatalog.FriendlyName` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation** — in `HaCatalog.cs`, change the first `using` block to include `System.Text.Json.Nodes`, then add the static method (e.g. just below `ObjectOf`):

Change the top of the file from:
```csharp
using Domain.Contracts;
```
to:
```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
```

Add this method to the `HaCatalog` record body:
```csharp
    public static string? FriendlyName(HaEntityState? entity) =>
        entity is not null
        && entity.Attributes.TryGetValue("friendly_name", out var value)
        && value is JsonValue jv
        && jv.TryGetValue<string>(out var name)
            ? name
            : null;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaCatalogTests"`
Expected: PASS (existing tests + the new one).

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaCatalog.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaCatalogTests.cs
git commit -m "feat(ha-vfs): HaCatalog.FriendlyName accessor"
```

---

## Task 3: `HaVfsPath.Parse` strips the nice-name suffix

**Files:**
- Modify: `Domain/Tools/HomeAssistant/Vfs/HaVfsPath.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaVfsPathTests.cs`

- [ ] **Step 1: Add the failing tests** (append these methods inside `HaVfsPathTests`)

```csharp
    [Fact]
    public void Parse_CompositeEntityDir_StripsNiceName()
    {
        var n = HaVfsPath.Parse("entities/climate/0x00158d00abcd_(aire-acondicionado-salon)");
        n.Kind.ShouldBe(HaVfsKind.EntityDir);
        n.EntityId.ShouldBe("climate.0x00158d00abcd");
    }

    [Fact]
    public void Parse_CompositeStateFile_StripsNiceName()
    {
        var n = HaVfsPath.Parse("entities/climate/0x00158d00abcd_(aire-acondicionado-salon)/state.json");
        n.Kind.ShouldBe(HaVfsKind.StateFile);
        n.EntityId.ShouldBe("climate.0x00158d00abcd");
    }

    [Fact]
    public void Parse_CompositeActionFile_UnderArea_StripsNiceName()
    {
        var n = HaVfsPath.Parse("areas/salon/climate.0x00158d00abcd_(aire-acondicionado-salon)/turn_off.sh");
        n.Kind.ShouldBe(HaVfsKind.ActionFile);
        n.EntityId.ShouldBe("climate.0x00158d00abcd");
        n.Service.ShouldBe("turn_off");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaVfsPathTests"`
Expected: FAIL — `EntityId` is `climate.0x00158d00abcd_(aire-acondicionado-salon)` (suffix not stripped) on the new tests. Existing tests still pass (bare paths are a no-op for `StripNice`).

- [ ] **Step 3: Write minimal implementation** — replace `ParseEntities` and `ParseAreas` in `HaVfsPath.cs` so the entity-dir segment is run through `HaSlug.StripNice`:

```csharp
    private static HaVfsNode ParseEntities(string[] s) => s.Length switch
    {
        1 => new HaVfsNode(HaVfsKind.EntitiesRoot),
        2 => new HaVfsNode(HaVfsKind.ClassDir, ClassDomain: s[1]),
        3 => new HaVfsNode(HaVfsKind.EntityDir, ClassDomain: s[1], EntityId: $"{s[1]}.{HaSlug.StripNice(s[2])}"),
        4 => Leaf(s[3], $"{s[1]}.{HaSlug.StripNice(s[2])}", area: null),
        _ => new HaVfsNode(HaVfsKind.Unknown)
    };

    private static HaVfsNode ParseAreas(string[] s) => s.Length switch
    {
        1 => new HaVfsNode(HaVfsKind.AreasRoot),
        2 => new HaVfsNode(HaVfsKind.AreaDir, Area: s[1]),
        3 => new HaVfsNode(HaVfsKind.EntityDir, Area: s[1], EntityId: HaSlug.StripNice(s[2])),
        4 => Leaf(s[3], HaSlug.StripNice(s[2]), area: s[1]),
        _ => new HaVfsNode(HaVfsKind.Unknown)
    };
```

(Leave `Parse`, `Leaf`, `StateFileName`, and the records unchanged. `HaSlug` is in the same namespace, so no `using` is needed.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaVfsPathTests"`
Expected: PASS (new composite tests + all existing bare-path tests).

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaVfsPath.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaVfsPathTests.cs
git commit -m "feat(ha-vfs): parse composite entity-dir segments to id"
```

---

## Task 4: `HaTree` emits composite segments

**Files:**
- Modify: `Domain/Tools/HomeAssistant/Vfs/HaTree.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaTreeTests.cs`
- Test (fix existing assertion): `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs`

- [ ] **Step 1: Add the failing tests** (append these inside `HaTreeTests`, and add `using System.Text.Json.Nodes;` to the top of the file)

```csharp
    [Fact]
    public void Directories_UseCompositeNameWhenFriendlyNamePresent()
    {
        var cat = new HaCatalog(
            [Entity("climate.0x00158d00abcd", "cool", ("friendly_name", JsonValue.Create("Aire Acondicionado Salón")))],
            [],
            [new HaAreaEntities("salon", "Salón", ["climate.0x00158d00abcd"])]);

        var dirs = HaTree.Directories(cat);

        dirs.ShouldContain("entities/climate/0x00158d00abcd_(aire-acondicionado-salon)");
        dirs.ShouldContain("areas/salon/climate.0x00158d00abcd_(aire-acondicionado-salon)");
    }

    [Fact]
    public void Files_UseCompositeDir()
    {
        var cat = new HaCatalog(
            [Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen Light")))],
            [Service("light", "turn_on", AnyEntityTarget())],
            []);

        var files = HaTree.Files(cat);

        files.ShouldContain("entities/light/kitchen_(kitchen-light)/state.json");
        files.ShouldContain("entities/light/kitchen_(kitchen-light)/turn_on.sh");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaTreeTests"`
Expected: FAIL — directories/files are still bare ids (`entities/climate/0x00158d00abcd`), so the composite `ShouldContain` assertions fail.

- [ ] **Step 3: Write minimal implementation** — in `HaTree.cs`, replace the entity-directory string construction in `Directories` and `Files` with `HaSlug.Compose(...)`. Replace the body of `Directories` and `Files` as follows (leave `LeafFiles`, `Glob`, `GlobToRegex` unchanged):

```csharp
    public static IReadOnlyList<string> Directories(HaCatalog catalog)
    {
        var dirs = new List<string> { "entities", "areas" };

        dirs.AddRange(catalog.ClassDomains().Select(c => $"entities/{c}"));
        dirs.AddRange(catalog.Entities.Select(e =>
            $"entities/{HaCatalog.ClassOf(e.EntityId)}/{HaSlug.Compose(HaCatalog.ObjectOf(e.EntityId), HaCatalog.FriendlyName(e))}"));

        foreach (var area in catalog.AreaSlugs())
        {
            dirs.Add($"areas/{area}");
            dirs.AddRange(catalog.EntityIdsInArea(area).Select(id =>
                $"areas/{area}/{HaSlug.Compose(id, HaCatalog.FriendlyName(catalog.EntityById(id)))}"));
        }

        return dirs.OrderBy(d => d, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<string> Files(HaCatalog catalog)
    {
        var files = new List<string>();

        foreach (var e in catalog.Entities)
        {
            var entDir = $"entities/{HaCatalog.ClassOf(e.EntityId)}/{HaSlug.Compose(HaCatalog.ObjectOf(e.EntityId), HaCatalog.FriendlyName(e))}";
            files.AddRange(LeafFiles(entDir, e.EntityId, catalog));
        }

        foreach (var area in catalog.AreaSlugs())
        {
            foreach (var id in catalog.EntityIdsInArea(area))
            {
                var entDir = $"areas/{area}/{HaSlug.Compose(id, HaCatalog.FriendlyName(catalog.EntityById(id)))}";
                files.AddRange(LeafFiles(entDir, id, catalog));
            }
        }

        return files.OrderBy(f => f, StringComparer.Ordinal).ToList();
    }
```

- [ ] **Step 4: Run the HaTree tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaTreeTests"`
Expected: PASS (new composite tests; existing tests use entities without `friendly_name`, so their bare paths are unchanged).

- [ ] **Step 5: Fix the one existing assertion that now expects a composite path** — in `HaFileSystemReadTests.cs`, the `Build` helper's `light.kitchen` entity has `friendly_name` "Kitchen", so glob now returns `entities/light/kitchen_(kitchen)`. Update `GlobAsync_Directories_ListsEntities`:

Change:
```csharp
        result.Select(n => n!.GetValue<string>()).ShouldContain("entities/light/kitchen");
```
to:
```csharp
        result.Select(n => n!.GetValue<string>()).ShouldContain("entities/light/kitchen_(kitchen)");
```

- [ ] **Step 6: Run the read tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaFileSystemReadTests"`
Expected: PASS (the updated glob assertion; `InfoAsync`/`ReadAsync` tests use bare paths which still resolve via `StripNice`).

- [ ] **Step 7: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaTree.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaTreeTests.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs
git commit -m "feat(ha-vfs): glob emits composite <id>_(<name>) directories"
```

---

## Task 5: End-to-end confirmation — disambiguation + composite resolution

**Files:**
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs`

These confirm the motivating scenario (two same-class entities are now distinguishable in `glob`) and that `exec` resolves a composite path. No production change — Tasks 3 and 4 already implement the behavior.

- [ ] **Step 1: Add the confirmation tests** (append inside `HaFileSystemReadTests`; the file already has `using System.Text.Json.Nodes;`, `Domain.Tools.Files`, `Microsoft.Extensions.Time.Testing`, and `using static ...FakeHaClient`)

```csharp
    [Fact]
    public async Task GlobAsync_TwoSameClassEntities_AreDistinguishableByName()
    {
        var client = new FakeHaClient
        {
            States =
            {
                Entity("climate.0x01", "cool", ("friendly_name", JsonValue.Create("Aire Acondicionado Salón"))),
                Entity("climate.0x02", "heat", ("friendly_name", JsonValue.Create("Calefacción Salón")))
            },
            AreaTemplateJson = """{"areas":[{"id":"salon","name":"Salón","entities":["climate.0x01","climate.0x02"]}]}"""
        };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);

        var hits = ((JsonArray)await fs.GlobAsync("areas/salon", "*", GlobMode.Directories, CancellationToken.None))
            .Select(n => n!.GetValue<string>()).ToList();

        hits.ShouldContain("areas/salon/climate.0x01_(aire-acondicionado-salon)");
        hits.ShouldContain("areas/salon/climate.0x02_(calefaccion-salon)");
    }

    [Fact]
    public async Task ExecAsync_ResolvesViaCompositePath()
    {
        var client = new FakeHaClient
        {
            States = { Entity("climate.0x01", "cool", ("friendly_name", JsonValue.Create("Aire Acondicionado Salón"))) },
            Services = { Service("climate", "turn_off", AnyEntityTarget()) },
            AreaTemplateJson = """{"areas":[{"id":"salon","name":"Salón","entities":["climate.0x01"]}]}"""
        };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);

        var result = await fs.ExecAsync(
            "areas/salon/climate.0x01_(aire-acondicionado-salon)", "turn_off.sh", null, CancellationToken.None);

        result["exitCode"]!.GetValue<int>().ShouldBe(0);
        client.LastCall!.Value.EntityId.ShouldBe("climate.0x01");
    }
```

- [ ] **Step 2: Run to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaFileSystemReadTests"`
Expected: PASS. (If `ExecAsync` is not accessible from `HaFileSystemReadTests`, it is — `HaFileSystem` is one partial class; no extra using needed.)

- [ ] **Step 3: Commit**

```bash
git add Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs
git commit -m "test(ha-vfs): glob disambiguation + composite-path exec"
```

---

## Task 6: Prompt note + journey update + full build/test

**Files:**
- Modify: `Domain/Prompts/HomeAssistantPrompt.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemJourneyTests.cs`

- [ ] **Step 1: Document the naming in the workflow prompt** — in `HomeAssistantPrompt.cs`, inside the `### Layout` section of `SystemPrompt`, add a bullet so the agent expects composite names and knows either form resolves. Change:

```
        - `/ha/areas/<room>/<entity_id>/` — the same entities grouped by room.
```
to:
```
        - `/ha/areas/<room>/<entity_id>/` — the same entities grouped by room.
        - Directory names read `<id>_(<friendly-name>)` (e.g.
          `climate.0x00158d00abcd_(aire-acondicionado-salon)`) so `glob` alone identifies a
          device — pick by the name. You may address a path by the full segment or by the bare
          `<id>`; both resolve.
```

- [ ] **Step 2: Extend the journey test to act on a composite path** — in `HaFileSystemJourneyTests.cs`, the `light.kitchen` entity has `friendly_name` "Kitchen", so its directory is `kitchen_(kitchen)` under `entities/light/`. After the existing step 4 (`act`), add a composite-addressing step before the area step:

Insert after the `act` block (the lines that assert `act["exitCode"]` and `brightness_pct`):
```csharp
        // 4b. the same action via the composite directory name resolves to the same entity
        var actByName = await fs.ExecAsync(
            "entities/light/kitchen_(kitchen)", "turn_on.sh --brightness_pct 30", null, CancellationToken.None);
        actByName["exitCode"]!.GetValue<int>().ShouldBe(0);
        client.LastCall!.Value.EntityId.ShouldBe("light.kitchen");
```

- [ ] **Step 3: Run the journey test**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaFileSystemJourneyTests"`
Expected: PASS.

- [ ] **Step 4: Full build + non-E2E test suite**

Run: `dotnet build`
Expected: SUCCESS, 0 errors.

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E"`
Expected: 0 failures. (E2E tests need a Docker browser stack unavailable in CI; they are pre-existing and unrelated.) Also confirm: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistant"` → 0 failures.

- [ ] **Step 5: Commit**

```bash
git add Domain/Prompts/HomeAssistantPrompt.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemJourneyTests.cs
git commit -m "feat(ha-vfs): document composite naming + journey coverage"
```

---

## Manual verification (after the plan)

Against the live stack (per `CLAUDE.md` → Launching, incl. `mcp-homeassistant`):
1. `glob_files /ha/areas/<room>` → entity directories now read `<entity_id>_(<friendly-name>)`.
2. The original scenario: two climate devices in one room appear as distinct, name-labelled directories; the agent can pick the AC without reading a file.
3. `exec` against the composite path (and against the bare id) both succeed with `exitCode: 0`.

## Self-Review

**Spec coverage:**
- Composite `<id>_(<name-slug>)` in both roots → Tasks 1, 4. ✓
- Slug rules (lowercase, accent-fold, non-alnum→`-`, trim, 60-char word-boundary cap) → Task 1. ✓
- Robust to adversarial names (id has no `(`; resolution keys on first `_(`; slug strips symbols) → Task 1 (`StripNice_AdversarialSuffix`, `Slugify` symbol cases). ✓
- `HaVfsPath.Parse` stays structural, strips suffix; bare id still resolves → Task 3 (+ existing bare tests stay green). ✓
- Generation from cached `friendly_name` → Tasks 2, 4. ✓
- Resolution unchanged in `HaFileSystem`; composite + bare both resolve for read/info/exec → Task 5 (+ Task 3 backward-compat). ✓
- Area directories unchanged (area slug) → Tasks 4 tests assert `areas/salon/<composite>` but the `salon` area dir itself stays the slug. ✓
- `state.json`, `.sh` model, search semantics, DI/wiring unchanged → not touched. ✓
- Optional workflow-prompt line → Task 6. ✓

**Placeholder scan:** none — every code/test step contains complete code and exact commands.

**Type consistency:** `HaSlug.Compose(string, string?)`, `HaSlug.StripNice(string)`, `HaSlug.Slugify(string?)`, `HaCatalog.FriendlyName(HaEntityState?)` — names used consistently in Tasks 3–6. `HaFileSystem`/`HaCatalogProvider`/`FakeHaClient`/`GlobMode` signatures match the existing code.

**Known coupling:** Task 4 changes glob output, which breaks one existing assertion (`HaFileSystemReadTests.GlobAsync_Directories_ListsEntities`); Task 4 Step 5 updates it. No other existing test uses an entity with a `friendly_name` in a path/glob assertion (verified: `HaVfsPathTests`/`HaTreeTests` entities have no `friendly_name`; the journey test addresses paths by bare id; search builds bare-id file paths and is unchanged).
