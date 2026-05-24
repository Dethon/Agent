# Shared Typed `fs_*` Result Contract — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every `fs_*` MCP tool a single typed result contract so backend payload shapes can no longer drift silently, enforced at compile time (shared DTOs), at the agent boundary (strict runtime validation), and via a published JSON Schema.

**Architecture:** Define one `record` per `fs_*` op in `Domain/DTOs/FileSystem/`, serialized through one shared `JsonSerializerOptions`. Refactor every producer (disk base tools in `Domain/Tools/{Files,Text}`, `Infrastructure/Clients/Bash/BashRunner`, and `Domain/Tools/HomeAssistant/Vfs/HaFileSystem`) to emit those records instead of hand-rolled `JsonObject`s. Add a strict validator at `McpFileSystemBackend` (the one MCP-client chokepoint every backend flows through) that converts a non-conforming payload to a `ToolError` envelope. Add a golden-file test that generates JSON Schema from the DTOs.

**Tech Stack:** .NET 10, System.Text.Json (`System.Text.Json.Nodes`, `System.Text.Json.Schema`), xUnit + Shouldly + Moq, ModelContextProtocol SDK.

**Spec:** `docs/superpowers/specs/2026-05-23-fs-typed-contract-design.md`

---

## Conventions for every task

- **No trailing newline** in any `.cs` file (repo-wide invariant — verified 226/226).
- DTOs are `public sealed record` with `required` init properties for mandatory
  fields and nullable properties for optional ones.
- Run a single test by fully-qualified name:
  `dotnet test --filter "FullyQualifiedName~<Class>.<Method>"`.
- Run a class: `dotnet test --filter "FullyQualifiedName~<Class>"`.
- Producers serialize via `FsResultContract.ToNode(record)` — never `new JsonObject`.
- Commit after each task (and after each RED→GREEN→REVIEW triplet) referencing the op.

## File map

**Create**
- `Domain/DTOs/FileSystem/FsResultContract.cs` — shared options, `ToNode`, `ResultTypes` map, `TryValidate`.
- `Domain/DTOs/FileSystem/FsReadResult.cs`, `FsInfoResult.cs`, `FsGlobResult.cs`,
  `FsSearchResult.cs` (+ nested), `FsExecResult.cs`, `FsCreateResult.cs`,
  `FsEditResult.cs` (+ nested), `FsMoveResult.cs`, `FsRemoveResult.cs`,
  `FsCopyResult.cs`, `FsBlobReadResult.cs`, `FsBlobWriteResult.cs`.
- `Tests/Unit/Domain/DTOs/FileSystem/FsResultContractTests.cs` — round-trip + strict-reject unit tests.
- `Tests/Unit/Infrastructure/Mcp/McpFileSystemBackendValidationTests.cs` — boundary guard tests (Domain-side validator unit tests live with the contract).
- `Tests/Unit/Domain/DTOs/FileSystem/FsSchemaGoldenTests.cs` — JSON Schema golden test.
- `docs/contracts/fs/*.schema.json` — generated, committed schema artifacts.

**Modify**
- `Domain/Tools/Text/TextReadTool.cs`, `TextCreateTool.cs`, `TextEditTool.cs`, `TextSearchTool.cs`.
- `Domain/Tools/Files/FileInfoTool.cs`, `GlobFilesTool.cs`, `CopyTool.cs`, `MoveTool.cs`, `RemoveTool.cs`, `BlobReadTool.cs`, `BlobWriteTool.cs`.
- `Infrastructure/Clients/Bash/BashRunner.cs`.
- `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs`, `HaFileSystem.Exec.cs`.
- `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`, `McpFileSystemDiscovery.cs`.
- `Domain/Tools/FileSystem/VfsGlobFilesTool.cs` (description), `Domain/Prompts/HomeAssistantPrompt.cs` (glob example) + any Vault/Sandbox filesystem prompt mentioning glob output.
- Existing glob tests: `Tests/Unit/Domain/Tools/GlobFilesToolTests.cs`, plus any HA glob and integration assertions found by grep.
- `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs` and `HaFileSystemExecTests.cs` — gain real-producer conformance assertions (`FsResultContract.TryValidate(...)`) for the five multi-producer ops (read/info/glob/search/exec). These drive the actual HA backend, so they are the genuine cross-backend no-drift guard (HA is the bespoke backend; disk producers are guaranteed by `ToNode` + the boundary guard + the schema test).

> **Conformance strategy.** Drift can only occur where ≥2 backends produce one op
> (read/info/glob/search/exec). Those are guarded by real-producer assertions on the
> HA tests (above) plus the disk producers' own shape tests. Single-producer disk ops
> are guaranteed structurally (`ToNode(record)`). All ops are additionally guarded at
> runtime by the strict boundary validator (Task 10) and in CI by the schema golden
> test (Task 11). There is intentionally **no** `ToNode→validate` tautology test.

---

## Task 1: Serialization contract + first DTO (read)

Establishes the pattern: shared options, `ToNode`, and round-trip/strict tests.

**Files:**
- Create: `Domain/DTOs/FileSystem/FsResultContract.cs`
- Create: `Domain/DTOs/FileSystem/FsReadResult.cs`
- Test: `Tests/Unit/Domain/DTOs/FileSystem/FsResultContractTests.cs`

- [ ] **Step 1: Write the failing test**

`Tests/Unit/Domain/DTOs/FileSystem/FsResultContractTests.cs`:

```csharp
using System.Text.Json;
using Domain.DTOs.FileSystem;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.FileSystem;

public class FsResultContractTests
{
    [Fact]
    public void ToNode_SerializesReadResult_WithCamelCaseAndOmittedNulls()
    {
        var node = FsResultContract.ToNode(new FsReadResult
        {
            FilePath = "/vault/a.md",
            Content = "1: hi",
            TotalLines = 1,
            Truncated = false
        });

        var json = node.ToJsonString();
        json.ShouldContain("\"filePath\":\"/vault/a.md\"");
        json.ShouldContain("\"totalLines\":1");
        json.ShouldNotContain("suggestion");
    }

    [Fact]
    public void TryValidate_AcceptsConformingPayload()
    {
        var node = FsResultContract.ToNode(new FsReadResult
        {
            FilePath = "/vault/a.md", Content = "x", TotalLines = 1, Truncated = false
        });

        FsResultContract.TryValidate("fs_read", node, out var error).ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Fact]
    public void TryValidate_RejectsExtraMember()
    {
        var node = JsonNodeWith("{\"filePath\":\"a\",\"content\":\"x\",\"totalLines\":1,\"truncated\":false,\"bogus\":true}");

        FsResultContract.TryValidate("fs_read", node, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void TryValidate_RejectsMissingRequiredMember()
    {
        var node = JsonNodeWith("{\"filePath\":\"a\",\"content\":\"x\"}");

        FsResultContract.TryValidate("fs_read", node, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_SkipsUnknownTool()
    {
        var node = JsonNodeWith("{\"anything\":1}");

        FsResultContract.TryValidate("fs_not_a_tool", node, out _).ShouldBeTrue();
    }

    private static System.Text.Json.Nodes.JsonNode JsonNodeWith(string json)
        => System.Text.Json.Nodes.JsonNode.Parse(json)!;
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~FsResultContractTests"`
Expected: FAIL — `FsResultContract` / `FsReadResult` do not exist (compile error).

- [ ] **Step 3: Write the DTO**

`Domain/DTOs/FileSystem/FsReadResult.cs`:

```csharp
namespace Domain.DTOs.FileSystem;

public sealed record FsReadResult
{
    public required string FilePath { get; init; }
    public required string Content { get; init; }
    public required int TotalLines { get; init; }
    public required bool Truncated { get; init; }
    public string? Suggestion { get; init; }
}
```

- [ ] **Step 4: Write the contract**

`Domain/DTOs/FileSystem/FsResultContract.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Domain.DTOs.FileSystem;

// Single source of truth for fs_* success-payload serialization and validation.
// Producers serialize through SerializerOptions (camelCase, omit nulls). The agent
// boundary and the conformance tests validate through ValidationOptions (strict:
// unknown members and missing required members both fail).
public static class FsResultContract
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static readonly JsonSerializerOptions ValidationOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static readonly IReadOnlyDictionary<string, Type> ResultTypes =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["fs_read"] = typeof(FsReadResult)
        };

    public static JsonNode ToNode<T>(T value) =>
        JsonSerializer.SerializeToNode(value, SerializerOptions)
        ?? throw new InvalidOperationException($"Failed to serialize {typeof(T).Name}");

    public static bool TryValidate(string toolName, JsonNode payload, out string? error)
    {
        error = null;
        if (!ResultTypes.TryGetValue(toolName, out var type))
        {
            return true;
        }

        try
        {
            payload.Deserialize(type, ValidationOptions);
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
```

> Note: STJ honours C# `required` members on deserialize (since .NET 7), so a
> missing required field throws `JsonException`. `UnmappedMemberHandling.Disallow`
> rejects extra fields. No extra config needed.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~FsResultContractTests"`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add Domain/DTOs/FileSystem/FsResultContract.cs Domain/DTOs/FileSystem/FsReadResult.cs Tests/Unit/Domain/DTOs/FileSystem/FsResultContractTests.cs
git commit -m "feat(fs-contract): serialization contract + FsReadResult"
```

---

## Task 2: Remaining DTOs + register them

Define every other result record and add them all to `ResultTypes`. Field names/types are taken verbatim from the current producers (see spec DTO catalog).

**Files:**
- Create: the eleven DTO files listed below.
- Modify: `Domain/DTOs/FileSystem/FsResultContract.cs` (extend `ResultTypes`).
- Test: `Tests/Unit/Domain/DTOs/FileSystem/FsResultContractTests.cs` (add a registration assertion).

- [ ] **Step 1: Write the failing test** (append to `FsResultContractTests`)

```csharp
    [Theory]
    [InlineData("fs_read")]
    [InlineData("fs_info")]
    [InlineData("fs_glob")]
    [InlineData("fs_search")]
    [InlineData("fs_exec")]
    [InlineData("fs_create")]
    [InlineData("fs_edit")]
    [InlineData("fs_move")]
    [InlineData("fs_remove")]
    [InlineData("fs_copy")]
    [InlineData("fs_blob_read")]
    [InlineData("fs_blob_write")]
    public void ResultTypes_CoversEveryFsTool(string toolName)
    {
        FsResultContract.ResultTypes.ShouldContainKey(toolName);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~FsResultContractTests.ResultTypes_CoversEveryFsTool"`
Expected: FAIL — only `fs_read` registered.

- [ ] **Step 3: Create the DTO files**

`Domain/DTOs/FileSystem/FsInfoResult.cs`:

```csharp
namespace Domain.DTOs.FileSystem;

public sealed record FsInfoResult
{
    public required bool Exists { get; init; }
    public required string Path { get; init; }
    public bool? IsDirectory { get; init; }
    public long? Size { get; init; }
    public string? LastModified { get; init; }
}
```

`Domain/DTOs/FileSystem/FsGlobResult.cs`:

```csharp
namespace Domain.DTOs.FileSystem;

public sealed record FsGlobResult
{
    public required IReadOnlyList<string> Entries { get; init; }
    public required bool Truncated { get; init; }
    public required int Total { get; init; }
}
```

`Domain/DTOs/FileSystem/FsSearchResult.cs`:

```csharp
namespace Domain.DTOs.FileSystem;

public sealed record FsSearchResult
{
    public required string Query { get; init; }
    public required bool Regex { get; init; }
    public required string Path { get; init; }
    public required int FilesSearched { get; init; }
    public required int FilesWithMatches { get; init; }
    public required int TotalMatches { get; init; }
    public required bool Truncated { get; init; }
    public required IReadOnlyList<FsSearchFileResult> Results { get; init; }
}

public sealed record FsSearchFileResult
{
    public required string File { get; init; }
    public int? MatchCount { get; init; }
    public IReadOnlyList<FsSearchMatch>? Matches { get; init; }
}

public sealed record FsSearchMatch
{
    public required int Line { get; init; }
    public required string Text { get; init; }
    public string? Section { get; init; }
    public FsSearchContext? Context { get; init; }
}

public sealed record FsSearchContext
{
    public required IReadOnlyList<string> Before { get; init; }
    public required IReadOnlyList<string> After { get; init; }
}
```

`Domain/DTOs/FileSystem/FsExecResult.cs`:

```csharp
namespace Domain.DTOs.FileSystem;

public sealed record FsExecResult
{
    public required string Stdout { get; init; }
    public required string Stderr { get; init; }
    public required int ExitCode { get; init; }
    public required bool Truncated { get; init; }
    public required bool TimedOut { get; init; }
    public required long DurationMs { get; init; }
    public required string Cwd { get; init; }
}
```

`Domain/DTOs/FileSystem/FsCreateResult.cs`:

```csharp
namespace Domain.DTOs.FileSystem;

public sealed record FsCreateResult
{
    public required string Status { get; init; }
    public required string FilePath { get; init; }
    public required string Size { get; init; }
    public required int Lines { get; init; }
}
```

`Domain/DTOs/FileSystem/FsEditResult.cs`:

```csharp
namespace Domain.DTOs.FileSystem;

public sealed record FsEditResult
{
    public required string Status { get; init; }
    public required string FilePath { get; init; }
    public required int TotalOccurrencesReplaced { get; init; }
    public required IReadOnlyList<FsEditDetail> Edits { get; init; }
}

public sealed record FsEditDetail
{
    public required int OccurrencesReplaced { get; init; }
    public required FsLineRange AffectedLines { get; init; }
}

public sealed record FsLineRange
{
    public required int Start { get; init; }
    public required int End { get; init; }
}
```

`Domain/DTOs/FileSystem/FsMoveResult.cs`:

```csharp
namespace Domain.DTOs.FileSystem;

public sealed record FsMoveResult
{
    public required string Status { get; init; }
    public required string Message { get; init; }
    public required string Source { get; init; }
    public required string Destination { get; init; }
}
```

`Domain/DTOs/FileSystem/FsRemoveResult.cs`:

```csharp
namespace Domain.DTOs.FileSystem;

public sealed record FsRemoveResult
{
    public required string Status { get; init; }
    public required string Message { get; init; }
    public required string OriginalPath { get; init; }
    public required string TrashPath { get; init; }
}
```

`Domain/DTOs/FileSystem/FsCopyResult.cs`:

```csharp
namespace Domain.DTOs.FileSystem;

public sealed record FsCopyResult
{
    public required string Status { get; init; }
    public required string Source { get; init; }
    public required string Destination { get; init; }
    public required long Bytes { get; init; }
}
```

`Domain/DTOs/FileSystem/FsBlobReadResult.cs`:

```csharp
namespace Domain.DTOs.FileSystem;

public sealed record FsBlobReadResult
{
    public required string ContentBase64 { get; init; }
    public required bool Eof { get; init; }
    public required long TotalBytes { get; init; }
}
```

`Domain/DTOs/FileSystem/FsBlobWriteResult.cs`:

```csharp
namespace Domain.DTOs.FileSystem;

public sealed record FsBlobWriteResult
{
    public required string Path { get; init; }
    public required int BytesWritten { get; init; }
    public required long TotalBytes { get; init; }
}
```

- [ ] **Step 4: Register all types** — replace the `ResultTypes` initializer in `FsResultContract.cs`:

```csharp
    public static readonly IReadOnlyDictionary<string, Type> ResultTypes =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["fs_read"] = typeof(FsReadResult),
            ["fs_info"] = typeof(FsInfoResult),
            ["fs_glob"] = typeof(FsGlobResult),
            ["fs_search"] = typeof(FsSearchResult),
            ["fs_exec"] = typeof(FsExecResult),
            ["fs_create"] = typeof(FsCreateResult),
            ["fs_edit"] = typeof(FsEditResult),
            ["fs_move"] = typeof(FsMoveResult),
            ["fs_remove"] = typeof(FsRemoveResult),
            ["fs_copy"] = typeof(FsCopyResult),
            ["fs_blob_read"] = typeof(FsBlobReadResult),
            ["fs_blob_write"] = typeof(FsBlobWriteResult)
        };
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~FsResultContractTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Domain/DTOs/FileSystem/ Tests/Unit/Domain/DTOs/FileSystem/FsResultContractTests.cs
git commit -m "feat(fs-contract): define all fs_* result DTOs and register them"
```

---

## Task 3: Refactor `read` producers (disk + HA) + conformance

`read` shapes already match; this is a safe refactor (no wire change) verified by
the existing real-producer tests plus a conformance assertion.

**Files:**
- Modify: `Domain/Tools/Text/TextReadTool.cs:49-63`
- Modify: `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs:241-261` (`BuildReadResult`)
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs`

- [ ] **Step 1: Add a real-producer conformance assertion**

`HaFileSystemReadTests.ReadAsync_StateFile_RendersFreshJson` already drives the real
`HaFileSystem.ReadAsync`. Append to it (add `using Domain.DTOs.FileSystem;`):

```csharp
        FsResultContract.TryValidate("fs_read", read, out var err).ShouldBeTrue(err);
```

- [ ] **Step 2: Run — passes today (characterization baseline)**

Run: `dotnet test --filter "FullyQualifiedName~HaFileSystemReadTests.ReadAsync_StateFile_RendersFreshJson"`
Expected: PASS — HA read already matches `FsReadResult`; this locks the shape before refactoring.

- [ ] **Step 3: Refactor `TextReadTool`** — replace lines 49-63 (`var result = new JsonObject { ... }` through `return result;`) with:

```csharp
        return FsResultContract.ToNode(new FsReadResult
        {
            FilePath = fullPath,
            Content = content,
            TotalLines = totalLines,
            Truncated = truncated,
            Suggestion = truncated
                ? $"File has more content. Use offset={startIndex + effectiveLimit + 1} to continue reading."
                : null
        });
```

Add `using Domain.DTOs.FileSystem;` at the top of the file.

- [ ] **Step 4: Refactor `HaFileSystem.BuildReadResult`** — replace the body (lines 241-261) with:

```csharp
    private static JsonNode BuildReadResult(string filePath, string text, int? offset, int? limit)
    {
        var allLines = text.Split('\n');
        var start = Math.Clamp((offset ?? 1) - 1, 0, allLines.Length);
        var remaining = allLines.Skip(start).ToArray();
        var take = Math.Min(limit ?? remaining.Length, remaining.Length);
        var content = string.Join("\n", remaining.Take(take).Select((l, i) => $"{start + i + 1}: {l}"));
        var truncated = take < remaining.Length;

        return FsResultContract.ToNode(new FsReadResult
        {
            FilePath = filePath,
            Content = content,
            TotalLines = allLines.Length,
            Truncated = truncated,
            Suggestion = truncated ? $"Use offset={start + take + 1} to continue reading." : null
        });
    }
```

Add `using Domain.DTOs.FileSystem;` to `HaFileSystem.cs`.

- [ ] **Step 5: Run existing read tests + conformance**

Run: `dotnet test --filter "FullyQualifiedName~HaFileSystemReadTests|FullyQualifiedName~VfsTextReadToolTests"`
Expected: PASS. If a read test asserted the literal JSON ordering, update it to assert fields via `node["filePath"]` etc. (semantics unchanged).

- [ ] **Step 6: Commit**

```bash
git add Domain/Tools/Text/TextReadTool.cs Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs
git commit -m "refactor(fs-contract): read producers emit FsReadResult"
```

---

## Task 4: Refactor `info` producers (disk + HA)

`info` is multi-shape today (not-exists vs dir vs file). The DTO's nullable fields cover all three; wire stays byte-identical (nulls omitted).

**Files:**
- Modify: `Domain/Tools/Files/FileInfoTool.cs` (the three `return new JsonObject` blocks)
- Modify: `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs:25-37` (`InfoAsync`)
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs`

- [ ] **Step 1: Add real-producer conformance assertions**

Append to the two existing HA info tests (`InfoAsync_EntityDir_Exists`,
`InfoAsync_MissingEntity_ExistsFalse`) — they drive the real `HaFileSystem.InfoAsync`:

```csharp
        FsResultContract.TryValidate("fs_info", info, out var err).ShouldBeTrue(err);
```

(In `InfoAsync_MissingEntity_ExistsFalse`, capture the result into a local `info`
first if it is currently inlined.)

- [ ] **Step 2: Run — passes today (characterization baseline)**

Run: `dotnet test --filter "FullyQualifiedName~HaFileSystemReadTests.InfoAsync_EntityDir_Exists|FullyQualifiedName~HaFileSystemReadTests.InfoAsync_MissingEntity_ExistsFalse"`
Expected: PASS — HA info already conforms (nullable size/lastModified omitted).

- [ ] **Step 3: Refactor `FileInfoTool`** — replace the three `return new JsonObject { ... }` blocks:

not-exists block:

```csharp
            return FsResultContract.ToNode(new FsInfoResult { Exists = false, Path = fullPath });
```

directory block:

```csharp
            var dirInfo = new DirectoryInfo(fullPath);
            return FsResultContract.ToNode(new FsInfoResult
            {
                Exists = true,
                Path = fullPath,
                IsDirectory = true,
                LastModified = dirInfo.LastWriteTimeUtc.ToString("O")
            });
```

file block:

```csharp
        var info = new FileInfo(fullPath);
        return FsResultContract.ToNode(new FsInfoResult
        {
            Exists = true,
            Path = fullPath,
            IsDirectory = false,
            Size = info.Length,
            LastModified = info.LastWriteTimeUtc.ToString("O")
        });
```

Add `using Domain.DTOs.FileSystem;` and remove the now-unused `using System.Text.Json.Nodes;` only if nothing else needs it (the method signature still returns `JsonNode`, so keep it).

- [ ] **Step 4: Refactor `HaFileSystem.InfoAsync`** — replace lines 31-36:

```csharp
        var result = new FsInfoResult { Exists = exists, Path = path, IsDirectory = exists ? isDir : null };
        return FsResultContract.ToNode(result);
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~VfsFileInfoToolTests|FullyQualifiedName~HaFileSystemReadTests"`
Expected: PASS. Update any info test asserting literal JSON to read fields by name.

- [ ] **Step 6: Commit**

```bash
git add Domain/Tools/Files/FileInfoTool.cs Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs
git commit -m "refactor(fs-contract): info producers emit FsInfoResult"
```

---

## Task 5: Refactor `glob` producers + the one wire change

`glob` becomes `{entries,truncated,total}` always. This is the only payload whose wire shape changes; update producers, prompt/description, and existing glob tests.

**Files:**
- Modify: `Domain/Tools/Files/GlobFilesTool.cs:73-98` (`RunDirectories`, `RunFiles`)
- Modify: `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs:18-23` (`GlobAsync`)
- Modify: `Domain/Tools/FileSystem/VfsGlobFilesTool.cs` (description), `Domain/Prompts/HomeAssistantPrompt.cs`
- Test: `Tests/Unit/Domain/Tools/GlobFilesToolTests.cs`, `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs`

- [ ] **Step 1: Find every glob-shape assertion**

Run:
```bash
grep -rn '"files"\|\["files"\]\|GlobAsync\|GlobDirectories\|GlobFiles' Tests --include='*.cs' | grep -v '/obj/' | grep -v '/bin/'
```
Expected: lists `GlobFilesToolTests`, possibly HA glob assertions and integration tests (`McpVaultServerTests`, `McpLibraryServerTests`, `McpAgentFileSystemTests`). Note them for Step 5.

- [ ] **Step 2: Rewrite the real-producer glob tests to the new shape**

In `Tests/Unit/Domain/Tools/GlobFilesToolTests.cs` (drives the real `GlobFilesTool`):
- Replace every `result.AsArray()` with `result["entries"]!.AsArray()`.
- In `Run_FilesMode_OverCap_ReturnsTruncatedObject`, replace `obj["files"]` with
  `obj["entries"]` and delete the two `obj["message"]` assertions (the message field
  is gone; `truncated`/`total` carry the meaning).
- After at least one success case, add: `FsResultContract.TryValidate("fs_glob", result, out var err).ShouldBeTrue(err);` (add `using Domain.DTOs.FileSystem;`).

In `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs`, the glob tests
cast to `(JsonArray)` — the new object shape breaks that. In
`GlobAsync_Directories_ListsEntities` and
`GlobAsync_TwoSameClassEntities_AreDistinguishableByName`, change
`((JsonArray)await fs.GlobAsync(...))` to read `result["entries"]!.AsArray()`:

```csharp
        var result = await fs.GlobAsync("entities/light", "*", GlobMode.Directories, CancellationToken.None);
        result["entries"]!.AsArray().Select(n => n!.GetValue<string>())
            .ShouldContain("entities/light/kitchen_(kitchen)");
        FsResultContract.TryValidate("fs_glob", result, out var err).ShouldBeTrue(err);
```

(Apply the analogous `result["entries"]` change to the two-entities test.)

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~GlobFilesToolTests|FullyQualifiedName~HaFileSystemReadTests"`
Expected: FAIL — disk producer still returns a bare array / `files` object; HA still returns a bare `JsonArray`.

- [ ] **Step 4: Refactor producers**

`GlobFilesTool.RunDirectories` (replace body):

```csharp
    private async Task<JsonNode> RunDirectories(string root, string pattern, CancellationToken cancellationToken)
    {
        var result = await client.GlobDirectories(root, pattern, cancellationToken);
        return FsResultContract.ToNode(new FsGlobResult
        {
            Entries = result,
            Truncated = false,
            Total = result.Length
        });
    }
```

`GlobFilesTool.RunFiles` (replace body):

```csharp
    private async Task<JsonNode> RunFiles(string root, string pattern, CancellationToken cancellationToken)
    {
        var result = await client.GlobFiles(root, pattern, cancellationToken);
        var capped = result.Length > FileResultCap;
        return FsResultContract.ToNode(new FsGlobResult
        {
            Entries = capped ? result.Take(FileResultCap).ToArray() : result,
            Truncated = capped,
            Total = result.Length
        });
    }
```

Add `using Domain.DTOs.FileSystem;`. Remove the now-unused `using System.Text.Json;` if `JsonSerializer` is no longer referenced elsewhere in the file (it isn't after this change — verify).

`HaFileSystem.GlobAsync` (replace lines 18-23 body):

```csharp
    public async Task<JsonNode> GlobAsync(string basePath, string pattern, GlobMode mode, CancellationToken ct)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        var hits = HaTree.Glob(catalog, basePath, pattern, mode == GlobMode.Directories);
        var entries = hits.ToList();
        return FsResultContract.ToNode(new FsGlobResult
        {
            Entries = entries,
            Truncated = false,
            Total = entries.Count
        });
    }
```

- [ ] **Step 5: Update descriptions/prompts and remaining tests**

- `VfsGlobFilesTool.ToolDescription`: change "In files mode, results are capped at 200." to "In files mode, results are capped at 200; the response is `{entries, truncated, total}`."
- `HomeAssistantPrompt`: update any glob example that shows a bare array to show `{entries: [...], truncated, total}`.
- Update HA glob assertions and integration assertions found in Step 1 to the new shape.

- [ ] **Step 6: Run the affected suites**

Run: `dotnet test --filter "FullyQualifiedName~GlobFilesToolTests|FullyQualifiedName~HaFileSystemReadTests|FullyQualifiedName~HaTreeTests"`
Expected: PASS. Then run integration glob tests if the environment allows.

- [ ] **Step 7: Commit**

```bash
git add Domain/Tools/Files/GlobFilesTool.cs Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs Domain/Tools/FileSystem/VfsGlobFilesTool.cs Domain/Prompts/HomeAssistantPrompt.cs Tests/
git commit -m "refactor(fs-contract): glob emits {entries,truncated,total} on all backends"
```

---

## Task 6: Refactor `search` producers (disk + HA)

Shapes already match except HA omits `section` (DTO makes it optional).

**Files:**
- Modify: `Domain/Tools/Text/TextSearchTool.cs:223-296` (`BuildResultJson` + the four `To*Json` helpers)
- Modify: `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs:120-191` (search result builders)
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs`

- [ ] **Step 1: Add a real-producer conformance assertion**

`HaFileSystemReadTests.SearchAsync_FindsEntityByState` drives the real
`HaFileSystem.SearchAsync`. Append (add `using Domain.DTOs.FileSystem;`):

```csharp
        FsResultContract.TryValidate("fs_search", result, out var err).ShouldBeTrue(err);
```

- [ ] **Step 2: Run — passes today (characterization baseline)**

Run: `dotnet test --filter "FullyQualifiedName~HaFileSystemReadTests.SearchAsync_FindsEntityByState"`
Expected: PASS — HA search already matches `FsSearchResult` (it modelled the disk shape).

- [ ] **Step 3: Refactor `TextSearchTool`** — replace `BuildResultJson` and helpers (lines 223-296) with a record-building version:

```csharp
    private static JsonNode BuildResultJson(
        string query,
        bool regex,
        string path,
        int filesSearched,
        List<FileMatch> results,
        int totalMatches,
        int maxResults,
        SearchOutputMode outputMode)
    {
        return FsResultContract.ToNode(new FsSearchResult
        {
            Query = query,
            Regex = regex,
            Path = path,
            FilesSearched = filesSearched,
            FilesWithMatches = results.Count,
            TotalMatches = totalMatches,
            Truncated = totalMatches >= maxResults,
            Results = results.Select(r => ToFileResult(r, outputMode)).ToList()
        });
    }

    private static FsSearchFileResult ToFileResult(FileMatch fileMatch, SearchOutputMode outputMode) =>
        outputMode == SearchOutputMode.FilesOnly
            ? new FsSearchFileResult { File = fileMatch.File, MatchCount = fileMatch.Matches.Count }
            : new FsSearchFileResult
            {
                File = fileMatch.File,
                Matches = fileMatch.Matches.Select(ToMatch).ToList()
            };

    private static FsSearchMatch ToMatch(MatchResult match)
    {
        var hasContext = match.ContextBefore?.Count > 0 || match.ContextAfter?.Count > 0;
        return new FsSearchMatch
        {
            Line = match.LineNumber,
            Text = match.Text,
            Section = match.Section,
            Context = hasContext
                ? new FsSearchContext
                {
                    Before = match.ContextBefore ?? [],
                    After = match.ContextAfter ?? []
                }
                : null
        };
    }
```

Add `using Domain.DTOs.FileSystem;`. Remove `ToFileMatchSummaryJson`, `ToFileMatchJson`, `ToMatchResultJson`, `ToJsonArray` (now replaced). Keep `using System.Text.Json.Nodes;` (return type is `JsonNode`); drop `JsonValue`/`JsonArray` usages.

- [ ] **Step 4: Refactor `HaFileSystem` search builders** — replace `BuildFileResult` (188-191), `BuildMatch` (168-186), and the final `return new JsonObject { ... }` (120-130) of `SearchAsync` so they build `FsSearchResult`. Concretely:

Replace the `results`/return at the end of `SearchAsync` (the `var results = new JsonArray();` accumulation and the closing `return new JsonObject {...}`) with a `List<FsSearchFileResult>` accumulator and:

```csharp
        return FsResultContract.ToNode(new FsSearchResult
        {
            Query = query,
            Regex = regex,
            Path = path ?? directoryPath ?? string.Empty,
            FilesSearched = scoped.Count,
            FilesWithMatches = filesWithMatches,
            TotalMatches = totalMatches,
            Truncated = totalMatches >= maxResults,
            Results = results
        });
```

Change `BuildFileResult` to return `FsSearchFileResult`:

```csharp
    private static FsSearchFileResult BuildFileResult(string file, IReadOnlyList<FsSearchMatch> matches, VfsTextSearchOutputMode outputMode) =>
        outputMode == VfsTextSearchOutputMode.FilesOnly
            ? new FsSearchFileResult { File = file, MatchCount = matches.Count }
            : new FsSearchFileResult { File = file, Matches = matches };
```

Change `FindMatches`/`BuildMatch` to produce `FsSearchMatch` (HA never sets `Section`):

```csharp
    private static List<FsSearchMatch> FindMatches(string[] lines, Regex matcher, int contextLines, int limit) =>
        lines
            .Select((text, index) => (text, index))
            .Where(l => matcher.IsMatch(l.text))
            .Take(limit)
            .Select(l => BuildMatch(lines, l.index, contextLines))
            .ToList();

    private static FsSearchMatch BuildMatch(string[] lines, int index, int contextLines)
    {
        if (contextLines <= 0)
        {
            return new FsSearchMatch { Line = index + 1, Text = lines[index] };
        }
        var before = lines.Take(index).TakeLast(contextLines).ToList();
        var after = lines.Skip(index + 1).Take(contextLines).ToList();
        var hasContext = before.Count > 0 || after.Count > 0;
        return new FsSearchMatch
        {
            Line = index + 1,
            Text = lines[index],
            Context = hasContext ? new FsSearchContext { Before = before, After = after } : null
        };
    }
```

Update the `results` accumulator type in `SearchAsync` from `JsonArray` to `List<FsSearchMatch>`/`List<FsSearchFileResult>` accordingly (the loop adds `BuildFileResult(...)`). Keep the early-return `ToolError.Create(...)` envelopes for bad-regex/timeout unchanged (they are error envelopes, not success payloads).

- [ ] **Step 5: Run search suites**

Run: `dotnet test --filter "FullyQualifiedName~HaFileSystemSearchTests|FullyQualifiedName~HaFileSystemReadTests|FullyQualifiedName~VfsTextSearchToolTests"`
Expected: PASS. Update literal-JSON assertions to read by field name.

- [ ] **Step 6: Commit**

```bash
git add Domain/Tools/Text/TextSearchTool.cs Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs
git commit -m "refactor(fs-contract): search producers emit FsSearchResult"
```

---

## Task 7: Improve HA `exec` + unify the exec contract (HA + BashRunner)

`FsExecResult` requires all 7 keys. BashRunner already emits them. HA exec is
**improved** to populate `cwd` (the entity-dir path), `durationMs` (measured), and
`timedOut` — and to actually HONOR the `timeoutSeconds` argument it currently
ignores. This is genuine new HA behaviour, so it gets real RED tests.

**Files:**
- Modify: `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.Exec.cs` (rewrite `ExecAsync` + `ExecResult`)
- Modify: `Infrastructure/Clients/Bash/BashRunner.cs:81-90`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemExecTests.cs`

- [ ] **Step 1: Write failing tests for the HA exec improvement**

Append to `HaFileSystemExecTests` (add `using Domain.DTOs.FileSystem;` and `using Domain.Contracts;`):

```csharp
    [Fact]
    public async Task ExecAsync_Success_ReportsCwdAndDuration()
    {
        var client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))) },
            Services = { Service("light", "turn_on", AnyEntityTarget()) }
        };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);

        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh", null, CancellationToken.None);

        result["exitCode"]!.GetValue<int>().ShouldBe(0);
        result["timedOut"]!.GetValue<bool>().ShouldBeFalse();
        result["cwd"]!.GetValue<string>().ShouldBe("entities/light/kitchen");
        result["durationMs"]!.GetValue<long>().ShouldBeGreaterThanOrEqualTo(0);
        FsResultContract.TryValidate("fs_exec", result, out var err).ShouldBeTrue(err);
    }

    [Fact]
    public async Task ExecAsync_HonorsTimeout_ReturnsTimedOut()
    {
        var client = new BlockingHaClient
        {
            States = { Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))) },
            Services = { Service("light", "turn_on", AnyEntityTarget()) }
        };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);

        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh", 1, CancellationToken.None);

        result["timedOut"]!.GetValue<bool>().ShouldBeTrue();
        result["exitCode"]!.GetValue<int>().ShouldBe(-1);
        FsResultContract.TryValidate("fs_exec", result, out _).ShouldBeTrue();
    }

    private sealed class BlockingHaClient : FakeHaClient
    {
        public override async Task<HaServiceCallResult> CallServiceAsync(
            string domain, string service, string? entityId,
            IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?>? data, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HaServiceCallResult { ChangedEntities = [] };
        }
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~HaFileSystemExecTests.ExecAsync_Success_ReportsCwdAndDuration|FullyQualifiedName~HaFileSystemExecTests.ExecAsync_HonorsTimeout_ReturnsTimedOut"`
Expected: FAIL — `cwd`/`durationMs`/`timedOut` absent, and the timeout is not honored.

> The timeout test will hang until Step 3 implements the timeout (the blocking
> client never returns). That hang IS the RED signal — implement Step 3, then run.

- [ ] **Step 3: Rewrite `HaFileSystem.ExecAsync` + `ExecResult`**

Replace the entire `ExecAsync` method and the `ExecResult` helper in
`Domain/Tools/HomeAssistant/Vfs/HaFileSystem.Exec.cs` with:

```csharp
    public async Task<JsonNode> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        JsonNode Done(int exitCode, string stdout, string stderr, bool timedOut = false) =>
            ExecResult(exitCode, stdout, stderr, timedOut, sw.ElapsedMilliseconds, path);

        var catalog = await catalogProvider.GetAsync(ct);
        var node = HaVfsPath.Parse(path);
        if (node.Kind != HaVfsKind.EntityDir || catalog.EntityById(node.EntityId!) is null)
        {
            return Done(127, "", $"Not an entity directory: {path}. cd into /ha/entities/<class>/<id> first.");
        }

        var tokens = ShellTokenize(command);
        var entityId = node.EntityId!;
        var actions = HaActionResolver.ServicesFor(entityId, catalog.Services);
        var available = string.Join(", ", actions.Select(a => $"{a.Service}.sh"));

        if (tokens.Count == 0)
        {
            return Done(127, "", $"No command. Available actions: {available}");
        }

        var script = tokens[0].StartsWith("./", StringComparison.Ordinal) ? tokens[0][2..] : tokens[0];
        if (!script.EndsWith(".sh", StringComparison.Ordinal))
        {
            return Done(127, "", $"command not found: {tokens[0]}. This filesystem only runs action files. Available actions: {available}");
        }

        var serviceName = script[..^3];
        var svc = actions.FirstOrDefault(a => a.Service.Equals(serviceName, StringComparison.Ordinal));
        if (svc is null)
        {
            return Done(127, "", $"command not found: {script}. Available actions: {available}");
        }

        var args = tokens.Skip(1).ToList();
        if (args.Contains("--help") || args.Contains("-h"))
        {
            return Done(0, HaServiceHelpRenderer.Render(entityId, svc), "");
        }

        JsonObject data;
        try
        {
            data = HaArgParser.Parse(args, svc);
        }
        catch (ArgumentException ex)
        {
            return Done(2, "", ex.Message);
        }

        using var timeoutCts = timeoutSeconds is > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        timeoutCts?.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds!.Value));
        var effectiveCt = timeoutCts?.Token ?? ct;

        try
        {
            IReadOnlyDictionary<string, JsonNode?> payload = data.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.DeepClone());
            var result = await clientFactory().CallServiceAsync(svc.Domain, svc.Service, entityId, payload, effectiveCt);
            var changed = new JsonArray(result.ChangedEntities
                .Select(e => (JsonNode?)$"{e.EntityId} → {e.State}").ToArray());
            var stdout = new JsonObject { ["ok"] = true, ["changed"] = changed };
            if (result.Response is not null)
            {
                stdout["response"] = result.Response.DeepClone();
            }
            return Done(0, stdout.ToJsonString(), "");
        }
        catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
        {
            return Done(-1, "", $"Action '{serviceName}.sh' timed out after {timeoutSeconds}s.", timedOut: true);
        }
        catch (HomeAssistantException ex)
        {
            return Done(1, "",
                $"{ex.Message}\nRe-check the field types with `{serviceName}.sh --help`; don't retry the same shape.");
        }
    }

    private static JsonNode ExecResult(int exitCode, string stdout, string stderr, bool timedOut, long durationMs, string cwd) =>
        FsResultContract.ToNode(new FsExecResult
        {
            Stdout = stdout,
            Stderr = stderr,
            ExitCode = exitCode,
            Truncated = false,
            TimedOut = timedOut,
            DurationMs = durationMs,
            Cwd = cwd
        });
```

Add `using System.Diagnostics;` and `using Domain.DTOs.FileSystem;` to the file. The
old `ExecResult(int, string, string)` signature is gone; all call sites now use
the local `Done(...)` wrapper.

- [ ] **Step 4: Refactor `BashRunner`** — replace lines 81-90:

```csharp
        return FsResultContract.ToNode(new FsExecResult
        {
            Stdout = stdoutResult.Text,
            Stderr = stderrResult.Text,
            ExitCode = timedOut ? -1 : process.ExitCode,
            TimedOut = timedOut,
            Truncated = stdoutResult.Truncated || stderrResult.Truncated,
            DurationMs = sw.ElapsedMilliseconds,
            Cwd = cwd
        });
```

Add `using Domain.DTOs.FileSystem;`.

- [ ] **Step 5: Run exec suites**

Run: `dotnet test --filter "FullyQualifiedName~HaFileSystemExecTests"`
Expected: PASS (including the two new tests). Update any pre-existing literal-JSON
exec assertion to read fields by name.

- [ ] **Step 6: Commit**

```bash
git add Infrastructure/Clients/Bash/BashRunner.cs Domain/Tools/HomeAssistant/Vfs/HaFileSystem.Exec.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemExecTests.cs
git commit -m "feat(fs-contract): improve HA exec (cwd, duration, honor timeout) + unify FsExecResult"
```

---

## Task 8: Refactor single-producer ops — create & edit

**Files:**
- Modify: `Domain/Tools/Text/TextCreateTool.cs:40-46`
- Modify: `Domain/Tools/Text/TextEditTool.cs:40-96`
- Test: existing `VfsTextCreateToolTests` / `VfsTextEditToolTests` (kept green)

> These are single-producer disk ops — no cross-backend drift is possible.
> Conformance is structural (the producer now returns `FsResultContract.ToNode(record)`)
> and enforced at runtime by the Task 10 boundary guard + the Task 11 schema. This
> task is a pure refactor verified by the existing suites staying green; no new RED.

- [ ] **Step 1: Refactor `TextCreateTool`** — replace lines 40-46:

```csharp
        return FsResultContract.ToNode(new FsCreateResult
        {
            Status = "created",
            FilePath = ToRelativePath(fullPath),
            Size = FormatFileSize(info.Length),
            Lines = content.Split('\n').Length
        });
```

Add `using Domain.DTOs.FileSystem;`.

- [ ] **Step 2: Refactor `TextEditTool`** — replace the per-edit accumulation and final return. Change `var perEditResults = new JsonArray();` to `var perEditResults = new List<FsEditDetail>();`; replace the `perEditResults.Add(new JsonObject { ... });` block (lines 75-83) with:

```csharp
            perEditResults.Add(new FsEditDetail
            {
                OccurrencesReplaced = replacedCount,
                AffectedLines = new FsLineRange { Start = startLine, End = endLine }
            });
```

Replace the final return (lines 90-96) with:

```csharp
        return FsResultContract.ToNode(new FsEditResult
        {
            Status = "success",
            FilePath = fullPath,
            TotalOccurrencesReplaced = totalReplaced,
            Edits = perEditResults
        });
```

Add `using Domain.DTOs.FileSystem;`.

- [ ] **Step 3: Run create/edit suites**

Run: `dotnet test --filter "FullyQualifiedName~VfsTextCreateToolTests|FullyQualifiedName~VfsTextEditToolTests"`
Expected: PASS. Update literal-JSON assertions to read by field name.

- [ ] **Step 4: Commit**

```bash
git add Domain/Tools/Text/TextCreateTool.cs Domain/Tools/Text/TextEditTool.cs Tests/
git commit -m "refactor(fs-contract): create/edit producers emit DTOs"
```

---

## Task 9: Refactor single-producer ops — move, remove, copy, blob read/write

**Files:**
- Modify: `Domain/Tools/Files/MoveTool.cs:27-33`, `RemoveTool.cs:24-30`, `CopyTool.cs:58-64`, `BlobReadTool.cs:53-58`, `BlobWriteTool.cs:49-54`
- Test: existing `VfsMoveToolTests` / `VfsRemoveToolTests` / `VfsCopyToolTests` / `McpFileSystemBackendChunkTests` (kept green)

> Single-producer disk ops — no cross-backend drift. Conformance is structural
> (`FsResultContract.ToNode(record)`) and enforced at runtime by the Task 10
> boundary guard + Task 11 schema. Pure refactor verified by existing suites; no new RED.

- [ ] **Step 1: Refactor each producer** (replace the `return new JsonObject { ... }` block; add `using Domain.DTOs.FileSystem;` to each file):

`MoveTool`:

```csharp
        return FsResultContract.ToNode(new FsMoveResult
        {
            Status = "success",
            Message = "File moved successfully",
            Source = sourcePath,
            Destination = destinationPath
        });
```

`RemoveTool`:

```csharp
        return FsResultContract.ToNode(new FsRemoveResult
        {
            Status = "success",
            Message = "Moved to trash",
            OriginalPath = path,
            TrashPath = trashPath
        });
```

`CopyTool`:

```csharp
        return FsResultContract.ToNode(new FsCopyResult
        {
            Status = "copied",
            Source = sourcePath,
            Destination = destinationPath,
            Bytes = bytes
        });
```

`BlobReadTool`:

```csharp
        return FsResultContract.ToNode(new FsBlobReadResult
        {
            ContentBase64 = Convert.ToBase64String(buffer, 0, actuallyRead),
            Eof = eof,
            TotalBytes = info.Length
        });
```

`BlobWriteTool`:

```csharp
        return FsResultContract.ToNode(new FsBlobWriteResult
        {
            Path = path,
            BytesWritten = bytes.Length,
            TotalBytes = info.Length
        });
```

- [ ] **Step 2: Run the relevant suites**

Run: `dotnet test --filter "FullyQualifiedName~VfsMoveToolTests|FullyQualifiedName~VfsRemoveToolTests|FullyQualifiedName~VfsCopyToolTests|FullyQualifiedName~McpFileSystemBackendChunkTests"`
Expected: PASS. Update literal-JSON assertions to read by field name. The blob-chunk tests (`McpFileSystemBackendChunkTests`) read `contentBase64`/`eof` — unchanged keys, should pass.

- [ ] **Step 3: Commit**

```bash
git add Domain/Tools/Files/MoveTool.cs Domain/Tools/Files/RemoveTool.cs Domain/Tools/Files/CopyTool.cs Domain/Tools/Files/BlobReadTool.cs Domain/Tools/Files/BlobWriteTool.cs Tests/
git commit -m "refactor(fs-contract): move/remove/copy/blob producers emit DTOs"
```

---

## Task 10: Strict boundary guard in `McpFileSystemBackend`

Validate every success payload against its DTO; on mismatch, log + return a `ToolError` envelope. The validator (`FsResultContract.TryValidate`) is already unit-tested (Task 1); here we wire it in and test the wiring.

**Files:**
- Modify: `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs:11-13` (ctor) and `:230-248` (success branch of `CallToolAsync`)
- Modify: `Infrastructure/Agents/Mcp/McpFileSystemDiscovery.cs:51` (pass logger)
- Test: `Tests/Unit/Infrastructure/Mcp/McpFileSystemBackendValidationTests.cs`

- [ ] **Step 1: Write the failing test**

`Tests/Unit/Infrastructure/Mcp/McpFileSystemBackendValidationTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Shouldly;

namespace Tests.Unit.Infrastructure.Mcp;

public class McpFileSystemBackendValidationTests
{
    [Fact]
    public void TryValidate_MalformedReadPayload_IsRejected()
    {
        var malformed = JsonNode.Parse("{\"filePath\":\"a\",\"content\":\"x\",\"totalLines\":1,\"truncated\":false,\"bogus\":1}")!;

        FsResultContract.TryValidate("fs_read", malformed, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void ConformingPayload_PassesValidation()
    {
        var ok = FsResultContract.ToNode(new FsGlobResult { Entries = ["a"], Truncated = false, Total = 1 });

        FsResultContract.TryValidate("fs_glob", ok, out _).ShouldBeTrue();
    }
}
```

> The boundary behaviour itself (envelope substitution) is tested through the
> Domain-side `FsResultContract.TryValidate` because `CallToolAsync` is
> `protected internal virtual` and existing tests override it; the validator is the
> unit under test. The wiring in Step 3 is exercised by the full integration
> suite in Task 12's final sweep.

- [ ] **Step 2: Run to verify it passes** (validator already exists)

Run: `dotnet test --filter "FullyQualifiedName~McpFileSystemBackendValidationTests"`
Expected: PASS — confirms the validator contract the wiring relies on.

- [ ] **Step 3: Wire the guard into `CallToolAsync`**

In `McpFileSystemBackend.cs`, change the constructor to accept an optional logger:

```csharp
internal class McpFileSystemBackend(McpClient client, string filesystemName, ILogger? logger = null) : IFileSystemBackend
```

Add `using Domain.DTOs.FileSystem;`, `using Domain.Tools;`, and `using Microsoft.Extensions.Logging;`.

Replace the final success return (currently `return parsed ?? throw ...;`, lines ~246-247) with:

```csharp
        var node = parsed
            ?? throw new InvalidOperationException($"Failed to parse response from {toolName}");

        if (!FsResultContract.TryValidate(toolName, node, out var validationError))
        {
            logger?.LogWarning(
                "Filesystem '{Filesystem}' returned a malformed '{Tool}' payload: {Error}",
                filesystemName, toolName, validationError);

            return ToolError.Create(
                ToolError.Codes.InternalError,
                $"The '{filesystemName}' filesystem returned a malformed '{toolName}' payload " +
                "that does not match the expected schema.",
                retryable: false,
                hint: "This is a backend bug; the payload was rejected to protect the conversation.");
        }

        return node;
```

- [ ] **Step 4: Pass the logger from discovery** — `McpFileSystemDiscovery.cs:51`:

```csharp
                    var backend = new McpFileSystemBackend(client, metadata.Name, logger);
```

- [ ] **Step 5: Build + run Mcp backend suites**

Run: `dotnet build` then `dotnet test --filter "FullyQualifiedName~McpFileSystemBackend"`
Expected: PASS (existing chunk/copy tests still pass; new validation tests pass).

- [ ] **Step 6: Commit**

```bash
git add Infrastructure/Agents/Mcp/McpFileSystemBackend.cs Infrastructure/Agents/Mcp/McpFileSystemDiscovery.cs Tests/Unit/Infrastructure/Mcp/McpFileSystemBackendValidationTests.cs
git commit -m "feat(fs-contract): strict boundary guard rejects malformed fs_* payloads"
```

---

## Task 11: JSON Schema golden-file test + committed artifacts

Generate JSON Schema from each DTO and commit it; a test keeps schema and DTO in lockstep.

**Files:**
- Create: `Tests/Unit/Domain/DTOs/FileSystem/FsSchemaGoldenTests.cs`
- Create: `docs/contracts/fs/*.schema.json` (written by the test on first run)

- [ ] **Step 1: Write the failing test**

`Tests/Unit/Domain/DTOs/FileSystem/FsSchemaGoldenTests.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Domain.DTOs.FileSystem;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.FileSystem;

public class FsSchemaGoldenTests
{
    // Set FS_SCHEMA_UPDATE=1 to (re)write the committed schema files.
    private static readonly string SchemaDir =
        Path.Combine(FindRepoRoot(), "docs", "contracts", "fs");

    public static IEnumerable<object[]> Schemas() =>
        FsResultContract.ResultTypes.Select(kvp => new object[] { kvp.Key, kvp.Value });

    [Theory]
    [MemberData(nameof(Schemas))]
    public void CommittedSchema_MatchesGeneratedFromDto(string toolName, Type dtoType)
    {
        var generated = FsResultContract.SerializerOptions
            .GetJsonSchemaAsNode(dtoType)
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        var file = Path.Combine(SchemaDir, $"{toolName}.schema.json");

        if (Environment.GetEnvironmentVariable("FS_SCHEMA_UPDATE") == "1")
        {
            Directory.CreateDirectory(SchemaDir);
            File.WriteAllText(file, generated);
            return;
        }

        File.Exists(file).ShouldBeTrue($"Missing schema {file}. Run with FS_SCHEMA_UPDATE=1 to generate.");
        var committed = File.ReadAllText(file);
        Normalize(committed).ShouldBe(Normalize(generated),
            $"Schema for {toolName} drifted from {dtoType.Name}. Run with FS_SCHEMA_UPDATE=1 to regenerate.");
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd();

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        return dir ?? throw new InvalidOperationException("Repo root not found");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~FsSchemaGoldenTests"`
Expected: FAIL — schema files do not exist yet.

- [ ] **Step 3: Generate the committed schemas**

Run: `FS_SCHEMA_UPDATE=1 dotnet test --filter "FullyQualifiedName~FsSchemaGoldenTests"`
Expected: PASS (writes `docs/contracts/fs/*.schema.json`).

- [ ] **Step 4: Re-run without the flag to confirm lock**

Run: `dotnet test --filter "FullyQualifiedName~FsSchemaGoldenTests"`
Expected: PASS — committed files match generated.

- [ ] **Step 5: Commit**

```bash
git add Tests/Unit/Domain/DTOs/FileSystem/FsSchemaGoldenTests.cs docs/contracts/fs/
git commit -m "feat(fs-contract): publish JSON Schema from DTOs with golden-file test"
```

---

## Task 12: Final sweep + docs

- [ ] **Step 1: Full build**

Run: `dotnet build`
Expected: 0 warnings, 0 errors. Fix any leftover unused `using`/`JsonArray` references in refactored producers.

- [ ] **Step 2: Full unit suite**

Run: `dotnet test --filter "Category!=E2E"`
Expected: all green. Investigate and fix any literal-JSON assertion that still expects the old glob shape or a hand-rolled key order.

- [ ] **Step 3: Update the spec status**

In `docs/superpowers/specs/2026-05-23-fs-typed-contract-design.md`, change `**Status:** Approved (design)` to `**Status:** Implemented`.

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-05-23-fs-typed-contract-design.md
git commit -m "docs(fs-contract): mark spec implemented"
```

---

## Self-review notes (for the executor)

- **Two intentional wire changes only: glob and HA exec.** `fs_glob` becomes
  `{entries,truncated,total}` on every backend; HA `fs_exec` gains
  `timedOut`/`durationMs`/`cwd` (and starts honoring `timeoutSeconds`). Every other
  op must serialize to a byte-for-byte equivalent of its current payload — if a
  producer's unit test that reads fields by name breaks, the DTO field name/casing
  is wrong; fix the DTO, not the test.
- **Error envelopes are never validated.** They flow through the `IsError == true`
  branch of `CallToolAsync` before the new guard. HA's `NotFound` / bad-regex /
  timeout returns are envelopes (`ok:false`) and must remain so.
- **`required` + `UnmappedMemberHandling.Disallow`** is what makes validation
  strict. If `GetJsonSchemaAsNode` or `required`-on-deserialize behaves unexpectedly
  on the .NET 10 target, the Task 1 / Task 11 tests will reveal it — do not weaken
  the options to make a test pass; fix the DTO or the producer.
- **No trailing newline** in any new `.cs` file.
