# Resilient `text_create` / `text_edit` Binding — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `text_create` and `text_edit` accept a JSON object/array/number/bool/null where a string is expected (coercing it to text) instead of failing at argument-binding time, and surface a `note` when coercion happens — identically across every mounted filesystem.

**Architecture:** Coercion is a per-parameter *lenient binder* installed on just these two tools via `AIFunctionFactoryOptions.ConfigureParameterBinding`. Parameter types stay `string` / `IReadOnlyList<TextEdit>`, so the advertised JSON schema is unchanged (`content`/`oldString`/`newString` remain `"string"`). The tool body detects coercion by inspecting the raw argument through an injected `AIFunctionArguments` and, on coercion, rebuilds the typed result with an optional `Note` member. All logic lives in the two tool bodies + the feature wiring — above the VFS registry — so every backend behaves identically and no backend changes.

**Tech Stack:** .NET 10, C#, Microsoft.Extensions.AI 10.6.0 (`AIFunctionFactory`, `AIFunctionFactoryOptions`, `AIFunctionArguments`), System.Text.Json, xUnit + Moq + Shouldly.

## Global Constraints

- `.cs` files have **no trailing newline** (`.editorconfig` `insert_final_newline = false`).
- The pre-commit hook runs `dotnet format` over staged `.cs` files and re-stages them **whole** — make the working tree match each commit.
- Domain layer only: no `Infrastructure`/`Agent` imports, no framework types beyond what's already referenced. `Microsoft.Extensions.AI.Abstractions` (which contains `AIFunctionFactory`, `AIFunctionFactoryOptions`, `AIFunctionArguments`) is already referenced by `Domain`.
- Prefer LINQ over loops. Records for DTOs. No XML doc comments; comment only "why".
- TDD: write the failing test, run it red, implement minimally, run green, commit.
- `Domain` has `InternalsVisibleTo("Tests")`, so `internal` helpers are directly testable.
- Do **not** change the domain `TextEdit` DTO or any `IFileSystemBackend` implementation. `filePath` and all other string params stay strictly bound.

## Verified facts (do not re-derive)

- `AIFunctionFactoryOptions.ConfigureParameterBinding` is `Func<ParameterInfo, AIFunctionFactoryOptions.ParameterBindingOptions>`.
- `ParameterBindingOptions` is a struct with `BindParameter` (`Func<ParameterInfo, AIFunctionArguments, object?>`) and `bool ExcludeFromSchema`. Return `default` for parameters you don't customize.
- Empirically confirmed against the real factory: with a custom `content` binder and an injected `AIFunctionArguments`, the generated schema is exactly `{"filePath":{"type":"string"},"content":{"type":"string"},"overwrite":{"type":"boolean"}}` — `content` stays `"string"`, and both the injected `AIFunctionArguments` and `CancellationToken` params are **auto-excluded** from the schema. Passing `content` as a JSON object binds without throwing, and the body sees the raw object via `arguments["content"]` (the arguments dictionary is not mutated by binding).
- `IVirtualFileSystemRegistry.Resolve(string) → FileSystemResolution(IFileSystemBackend Backend, string RelativePath, string MountPoint = "")`.
- `IFileSystemBackend.CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct) → Task<FsResult<FsCreateResult>>`.
- `IFileSystemBackend.EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct) → Task<FsResult<FsEditResult>>`.
- `FsResult<T>.Ok(T Value)` (nested record); `FsResult<T>.TryGetValue(out T? value, out ToolErrorResult? error)`.
- `FsResultContract.SerializerOptions` omits nulls (`WhenWritingNull`); `ValidationOptions` uses `UnmappedMemberHandling.Disallow` (unknown members fail; missing optional members are fine).
- No code calls `VfsTextCreateTool.RunAsync` / `VfsTextEditTool.RunAsync` positionally; the new optional `arguments` parameter is binding-safe.

## File Structure

- Create `Domain/Tools/FileSystem/TextArg.cs` — coercion helper (`Coerce`, `WasCoerced`, `WasCoercedArg`, `CoerceEdits`, `EditsWereCoercedArg`).
- Modify `Domain/DTOs/FileSystem/FsCreateResult.cs`, `Domain/DTOs/FileSystem/FsEditResult.cs` — add optional `string? Note`.
- Modify `Domain/Tools/FileSystem/VfsTextCreateTool.cs`, `Domain/Tools/FileSystem/VfsTextEditTool.cs` — inject `AIFunctionArguments`, attach note on coercion.
- Modify `Domain/Tools/FileSystem/FileSystemToolFeature.cs` — build the two tools with a lenient `ConfigureParameterBinding`.
- Create `Tests/Unit/Domain/Tools/FileSystem/TextArgTests.cs`, `Tests/Unit/Domain/Tools/FileSystem/VfsTextCreateToolTests.cs`, `Tests/Unit/Domain/Tools/FileSystem/VfsTextEditToolTests.cs`.

---

### Task 1: `TextArg` coercion helper

**Files:**
- Create: `Domain/Tools/FileSystem/TextArg.cs`
- Test: `Tests/Unit/Domain/Tools/FileSystem/TextArgTests.cs`

**Interfaces:**
- Produces:
  - `internal static string TextArg.Coerce(object? raw)`
  - `internal static bool TextArg.WasCoerced(object? raw)`
  - `internal static bool TextArg.WasCoercedArg(AIFunctionArguments? arguments, string key)`
  - `internal static IReadOnlyList<TextEdit> TextArg.CoerceEdits(object? raw)`
  - `internal static bool TextArg.EditsWereCoercedArg(AIFunctionArguments? arguments)`

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/Domain/Tools/FileSystem/TextArgTests.cs`:

```csharp
using System.Text.Json;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class TextArgTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Theory]
    [InlineData("\"hello\"", "hello", false)]
    [InlineData("{\"a\":1}", "{\"a\":1}", true)]
    [InlineData("[1,2]", "[1,2]", true)]
    [InlineData("42", "42", true)]
    [InlineData("true", "true", true)]
    [InlineData("null", "", true)]
    public void Coerce_And_WasCoerced_ByValueKind(string rawJson, string expectedText, bool expectedCoerced)
    {
        var raw = Json(rawJson);
        TextArg.Coerce(raw).ShouldBe(expectedText);
        TextArg.WasCoerced(raw).ShouldBe(expectedCoerced);
    }

    [Fact]
    public void Coerce_BareClrString_PassesThrough_NotCoerced()
    {
        TextArg.Coerce("x").ShouldBe("x");
        TextArg.WasCoerced("x").ShouldBeFalse();
    }

    [Fact]
    public void Coerce_Null_IsEmpty_NotCoerced()
    {
        TextArg.Coerce(null).ShouldBe(string.Empty);
        TextArg.WasCoerced(null).ShouldBeFalse();
    }

    [Fact]
    public void WasCoercedArg_TrueWhenPresentArgIsObject_FalseWhenAbsentOrString()
    {
        var args = new AIFunctionArguments { ["content"] = Json("{\"a\":1}") };
        TextArg.WasCoercedArg(args, "content").ShouldBeTrue();

        var stringArgs = new AIFunctionArguments { ["content"] = Json("\"hi\"") };
        TextArg.WasCoercedArg(stringArgs, "content").ShouldBeFalse();

        TextArg.WasCoercedArg(new AIFunctionArguments(), "content").ShouldBeFalse();
        TextArg.WasCoercedArg(null, "content").ShouldBeFalse();
    }

    [Fact]
    public void CoerceEdits_CoercesStructuredFields_KeepsStrings_AndReplaceAll()
    {
        var raw = Json("[{\"oldString\":\"a\",\"newString\":{\"k\":1}},{\"oldString\":\"b\",\"newString\":\"c\",\"replaceAll\":true}]");
        var edits = TextArg.CoerceEdits(raw);

        edits.Count.ShouldBe(2);
        edits[0].OldString.ShouldBe("a");
        edits[0].NewString.ShouldBe("{\"k\":1}");
        edits[0].ReplaceAll.ShouldBeFalse();
        edits[1].OldString.ShouldBe("b");
        edits[1].NewString.ShouldBe("c");
        edits[1].ReplaceAll.ShouldBeTrue();
    }

    [Fact]
    public void CoerceEdits_NonArray_ReturnsEmpty()
    {
        TextArg.CoerceEdits(Json("{\"not\":\"an array\"}")).ShouldBeEmpty();
        TextArg.CoerceEdits(null).ShouldBeEmpty();
    }

    [Fact]
    public void EditsWereCoercedArg_TrueWhenAnyFieldStructured()
    {
        var coerced = new AIFunctionArguments { ["edits"] = Json("[{\"oldString\":\"a\",\"newString\":{\"k\":1}}]") };
        TextArg.EditsWereCoercedArg(coerced).ShouldBeTrue();

        var clean = new AIFunctionArguments { ["edits"] = Json("[{\"oldString\":\"a\",\"newString\":\"b\"}]") };
        TextArg.EditsWereCoercedArg(clean).ShouldBeFalse();

        TextArg.EditsWereCoercedArg(new AIFunctionArguments()).ShouldBeFalse();
        TextArg.EditsWereCoercedArg(null).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TextArgTests"`
Expected: FAIL to compile — `TextArg` does not exist.

- [ ] **Step 3: Create the helper**

Create `Domain/Tools/FileSystem/TextArg.cs`:

```csharp
using System.Text.Json;
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

// Lenient coercion for arguments that are declared as text but may arrive as structured
// JSON when the model misuses a tool (e.g. passing a JSON object as a *.json file's
// content). Keeps text_create / text_edit resilient without weakening their advertised
// "string" schema: parameter types are unchanged; only the binding is made tolerant.
internal static class TextArg
{
    public static string Coerce(object? raw) => raw switch
    {
        JsonElement element => CoerceElement(element),
        string text => text,
        null => string.Empty,
        _ => raw.ToString() ?? string.Empty
    };

    public static bool WasCoerced(object? raw) => raw switch
    {
        JsonElement element => element.ValueKind is not JsonValueKind.String,
        string => false,
        null => false,
        _ => true
    };

    public static bool WasCoercedArg(AIFunctionArguments? arguments, string key) =>
        arguments is not null && arguments.TryGetValue(key, out var raw) && WasCoerced(raw);

    public static IReadOnlyList<TextEdit> CoerceEdits(object? raw)
    {
        if (raw is not JsonElement array || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Select(element => new TextEdit(
                Coerce(element.TryGetProperty("oldString", out var oldString) ? oldString : default),
                Coerce(element.TryGetProperty("newString", out var newString) ? newString : default),
                element.TryGetProperty("replaceAll", out var replaceAll) && replaceAll.ValueKind == JsonValueKind.True))
            .ToList();
    }

    public static bool EditsWereCoercedArg(AIFunctionArguments? arguments)
    {
        if (arguments is null || !arguments.TryGetValue("edits", out var raw)
            || raw is not JsonElement array || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return array.EnumerateArray()
            .Any(element => IsStructuredProperty(element, "oldString") || IsStructuredProperty(element, "newString"));
    }

    private static bool IsStructuredProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.String;

    private static string CoerceElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => element.GetRawText()
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TextArgTests"`
Expected: PASS (all cases).

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/FileSystem/TextArg.cs Tests/Unit/Domain/Tools/FileSystem/TextArgTests.cs
git commit -m "feat(filesystem): TextArg helper coerces structured JSON args to text

Claude-Session: https://claude.ai/code/session_01SeYwcPTCgGLGzoXte3dgfo"
```

---

### Task 2: Optional `Note` on the create/edit result contracts

**Files:**
- Modify: `Domain/DTOs/FileSystem/FsCreateResult.cs`
- Modify: `Domain/DTOs/FileSystem/FsEditResult.cs`
- Test: `Tests/Unit/Domain/DTOs/FileSystem/FsResultContractTests.cs` (add cases)

**Interfaces:**
- Produces: `FsCreateResult.Note` and `FsEditResult.Note`, both `string?` (`init`), omitted from JSON when null.

- [ ] **Step 1: Write the failing tests**

Append to `Tests/Unit/Domain/DTOs/FileSystem/FsResultContractTests.cs` (inside the class):

```csharp
    [Fact]
    public void CreateResult_OmitsNote_WhenNull_AndValidates()
    {
        var node = FsResultContract.ToNode(new FsCreateResult
        {
            Status = "created", FilePath = "/vault/a.md", Size = "3 B", Lines = 1
        });

        node.ToJsonString().ShouldNotContain("note");
        FsResultContract.TryValidate("fs_create", node, out var error).ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Fact]
    public void CreateResult_IncludesNote_WhenSet_AndValidates()
    {
        var node = FsResultContract.ToNode(new FsCreateResult
        {
            Status = "created", FilePath = "/vault/a.md", Size = "3 B", Lines = 1, Note = "coerced"
        });

        node.ToJsonString().ShouldContain("\"note\":\"coerced\"");
        FsResultContract.TryValidate("fs_create", node, out _).ShouldBeTrue();
    }

    [Fact]
    public void EditResult_WithNote_Validates()
    {
        var node = FsResultContract.ToNode(new FsEditResult
        {
            Status = "edited", FilePath = "/vault/a.md", TotalOccurrencesReplaced = 1,
            Edits = [new FsEditDetail { OccurrencesReplaced = 1, AffectedLines = new FsLineRange { Start = 1, End = 1 } }],
            Note = "coerced"
        });

        node.ToJsonString().ShouldContain("\"note\":\"coerced\"");
        FsResultContract.TryValidate("fs_edit", node, out _).ShouldBeTrue();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FsResultContractTests"`
Expected: FAIL to compile — `FsCreateResult`/`FsEditResult` have no `Note` member.

- [ ] **Step 3: Add the optional members**

Edit `Domain/DTOs/FileSystem/FsCreateResult.cs`:

```csharp
namespace Domain.DTOs.FileSystem;

public sealed record FsCreateResult
{
    public required string Status { get; init; }
    public required string FilePath { get; init; }
    public required string Size { get; init; }
    public required int Lines { get; init; }
    public string? Note { get; init; }
}
```

Edit `Domain/DTOs/FileSystem/FsEditResult.cs` — add `Note` to `FsEditResult` only (leave `FsEditDetail`/`FsLineRange` unchanged):

```csharp
public sealed record FsEditResult
{
    public required string Status { get; init; }
    public required string FilePath { get; init; }
    public required int TotalOccurrencesReplaced { get; init; }
    public required IReadOnlyList<FsEditDetail> Edits { get; init; }
    public string? Note { get; init; }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FsResultContractTests"`
Expected: PASS (new cases + all pre-existing cases).

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/FileSystem/FsCreateResult.cs Domain/DTOs/FileSystem/FsEditResult.cs Tests/Unit/Domain/DTOs/FileSystem/FsResultContractTests.cs
git commit -m "feat(filesystem): optional Note on FsCreateResult/FsEditResult

Claude-Session: https://claude.ai/code/session_01SeYwcPTCgGLGzoXte3dgfo"
```

---

### Task 3: `text_create` tolerates object content

**Files:**
- Modify: `Domain/Tools/FileSystem/VfsTextCreateTool.cs`
- Modify: `Domain/Tools/FileSystem/FileSystemToolFeature.cs:26-27` (the `text_create` factory entry)
- Test: `Tests/Unit/Domain/Tools/FileSystem/VfsTextCreateToolTests.cs`

**Interfaces:**
- Consumes: `TextArg.WasCoercedArg` (Task 1), `FsCreateResult.Note` (Task 2).
- Produces: `text_create` AIFunction whose `content` binder coerces via `TextArg.Coerce`; `VfsTextCreateTool.RunAsync(string filePath, string content, bool overwrite = false, bool createDirectories = true, AIFunctionArguments? arguments = null, CancellationToken cancellationToken = default)`.

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/Domain/Tools/FileSystem/VfsTextCreateToolTests.cs`:

```csharp
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsTextCreateToolTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static (Mock<IVirtualFileSystemRegistry> Registry, Mock<IFileSystemBackend> Backend) Wire(
        Action<string>? captureContent = null)
    {
        var backend = new Mock<IFileSystemBackend>();
        backend
            .Setup(b => b.CreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, bool, CancellationToken>((_, content, _, _, _) => captureContent?.Invoke(content))
            .ReturnsAsync(new FsResult<FsCreateResult>.Ok(new FsCreateResult
            {
                Status = "created", FilePath = "/schedules/x/schedule.json", Size = "34 B", Lines = 1
            }));

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve(It.IsAny<string>()))
            .Returns<string>(path => new FileSystemResolution(backend.Object, path));
        registry.Setup(r => r.GetMounts())
            .Returns([new FileSystemMount("schedules", "/schedules", "Scheduled tasks")]);
        return (registry, backend);
    }

    [Fact]
    public async Task Body_AddsNote_WhenContentArgWasObject()
    {
        var (registry, _) = Wire();
        var tool = new VfsTextCreateTool(registry.Object);
        var args = new AIFunctionArguments { ["content"] = Json("{\"a\":1}") };

        var node = await tool.RunAsync("/schedules/x/schedule.json", "{\"a\":1}", arguments: args);

        node.ToJsonString().ShouldContain("\"note\"");
    }

    [Fact]
    public async Task Body_NoNote_WhenContentArgWasString()
    {
        var (registry, _) = Wire();
        var tool = new VfsTextCreateTool(registry.Object);
        var args = new AIFunctionArguments { ["content"] = Json("\"hello\"") };

        var node = await tool.RunAsync("/vault/a.md", "hello", arguments: args);

        node.ToJsonString().ShouldNotContain("note");
    }

    [Fact]
    public async Task Factory_ObjectContent_Binds_WritesJsonText_AndNotes()
    {
        string? captured = null;
        var (registry, _) = Wire(c => captured = c);
        var create = new FileSystemToolFeature(registry.Object)
            .GetTools(new FeatureConfig())
            .Single(t => t.Name == "domain__filesystem__text_create");

        var args = new AIFunctionArguments
        {
            ["filePath"] = Json("\"/schedules/x/schedule.json\""),
            ["content"] = Json("{\"cron\":\"0 9 * * *\",\"prompt\":\"hi\"}")
        };
        var result = await create.InvokeAsync(args);

        captured.ShouldBe("{\"cron\":\"0 9 * * *\",\"prompt\":\"hi\"}");
        JsonSerializer.Serialize(result).ShouldContain("note");
    }

    [Fact]
    public void Factory_Schema_KeepsContentString_AndHidesInjectedParams()
    {
        var (registry, _) = Wire();
        var create = new FileSystemToolFeature(registry.Object)
            .GetTools(new FeatureConfig())
            .Single(t => t.Name == "domain__filesystem__text_create");

        var properties = create.JsonSchema.GetProperty("properties");
        properties.GetProperty("content").GetProperty("type").GetString().ShouldBe("string");
        properties.TryGetProperty("arguments", out _).ShouldBeFalse();
        properties.TryGetProperty("cancellationToken", out _).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsTextCreateToolTests"`
Expected: FAIL to compile — `RunAsync` has no `arguments` parameter yet, and the factory does not coerce.

- [ ] **Step 3a: Update the tool body**

Replace `Domain/Tools/FileSystem/VfsTextCreateTool.cs` with:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

public class VfsTextCreateTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "create";
    public const string Name = "text_create";

    private const string CoercionNote =
        "The 'content' argument arrived as structured JSON rather than a string; its JSON text was written. Pass 'content' as a string next time.";

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
        AIFunctionArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(filePath);
        var result = await resolution.Backend.CreateAsync(
            resolution.RelativePath, content, overwrite, createDirectories, cancellationToken);

        if (TextArg.WasCoercedArg(arguments, "content") && result.TryGetValue(out var value, out _))
        {
            return FsResultContract.ToNode(value with { Note = CoercionNote });
        }

        return result.ToNode();
    }
}
```

- [ ] **Step 3b: Wire the lenient binder**

In `Domain/Tools/FileSystem/FileSystemToolFeature.cs`, replace the `text_create` tuple entry (currently line 27) with:

```csharp
            (VfsTextCreateTool.Key, () => AIFunctionFactory.Create(
                new VfsTextCreateTool(registry).RunAsync,
                new AIFunctionFactoryOptions
                {
                    Name = $"domain__{Feature}__{VfsTextCreateTool.Name}",
                    ConfigureParameterBinding = parameter => parameter.Name == "content"
                        ? new AIFunctionFactoryOptions.ParameterBindingOptions
                        {
                            BindParameter = (_, args) =>
                                TextArg.Coerce(args.TryGetValue("content", out var raw) ? raw : null)
                        }
                        : default
                })),
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsTextCreateToolTests"`
Expected: PASS (all four).

Then confirm no regression in the feature test:
Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FileSystemToolFeatureTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/FileSystem/VfsTextCreateTool.cs Domain/Tools/FileSystem/FileSystemToolFeature.cs Tests/Unit/Domain/Tools/FileSystem/VfsTextCreateToolTests.cs
git commit -m "feat(filesystem): text_create coerces object content to JSON text, notes it

Claude-Session: https://claude.ai/code/session_01SeYwcPTCgGLGzoXte3dgfo"
```

---

### Task 4: `text_edit` tolerates object oldString/newString

**Files:**
- Modify: `Domain/Tools/FileSystem/VfsTextEditTool.cs`
- Modify: `Domain/Tools/FileSystem/FileSystemToolFeature.cs:28` (the `text_edit` factory entry)
- Test: `Tests/Unit/Domain/Tools/FileSystem/VfsTextEditToolTests.cs`

**Interfaces:**
- Consumes: `TextArg.CoerceEdits`, `TextArg.EditsWereCoercedArg` (Task 1), `FsEditResult.Note` (Task 2).
- Produces: `text_edit` AIFunction whose `edits` binder coerces via `TextArg.CoerceEdits`; `VfsTextEditTool.RunAsync(string filePath, IReadOnlyList<TextEdit> edits, AIFunctionArguments? arguments = null, CancellationToken cancellationToken = default)`.

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/Domain/Tools/FileSystem/VfsTextEditToolTests.cs`:

```csharp
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsTextEditToolTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static (Mock<IVirtualFileSystemRegistry> Registry, List<TextEdit>? Captured) WireCapturing(out Func<List<TextEdit>?> read)
    {
        List<TextEdit>? captured = null;
        var backend = new Mock<IFileSystemBackend>();
        backend
            .Setup(b => b.EditAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<TextEdit>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<TextEdit>, CancellationToken>((_, edits, _) => captured = edits.ToList())
            .ReturnsAsync(new FsResult<FsEditResult>.Ok(new FsEditResult
            {
                Status = "edited", FilePath = "/vault/a.md", TotalOccurrencesReplaced = 1,
                Edits = [new FsEditDetail { OccurrencesReplaced = 1, AffectedLines = new FsLineRange { Start = 1, End = 1 } }]
            }));

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve(It.IsAny<string>()))
            .Returns<string>(path => new FileSystemResolution(backend.Object, path));
        registry.Setup(r => r.GetMounts())
            .Returns([new FileSystemMount("vault", "/vault", "Vault")]);

        read = () => captured;
        return (registry, captured);
    }

    [Fact]
    public async Task Factory_ObjectNewString_Binds_UsesJsonText_AndNotes()
    {
        var (registry, _) = WireCapturing(out var read);
        var edit = new FileSystemToolFeature(registry.Object)
            .GetTools(new FeatureConfig())
            .Single(t => t.Name == "domain__filesystem__text_edit");

        var args = new AIFunctionArguments
        {
            ["filePath"] = Json("\"/vault/a.md\""),
            ["edits"] = Json("[{\"oldString\":\"a\",\"newString\":{\"k\":1}}]")
        };
        var result = await edit.InvokeAsync(args);

        var captured = read();
        captured.ShouldNotBeNull();
        captured!.Single().NewString.ShouldBe("{\"k\":1}");
        JsonSerializer.Serialize(result).ShouldContain("note");
    }

    [Fact]
    public async Task Factory_AllStringEdits_NoNote()
    {
        var (registry, _) = WireCapturing(out _);
        var edit = new FileSystemToolFeature(registry.Object)
            .GetTools(new FeatureConfig())
            .Single(t => t.Name == "domain__filesystem__text_edit");

        var args = new AIFunctionArguments
        {
            ["filePath"] = Json("\"/vault/a.md\""),
            ["edits"] = Json("[{\"oldString\":\"a\",\"newString\":\"b\"}]")
        };
        var result = await edit.InvokeAsync(args);

        JsonSerializer.Serialize(result).ShouldNotContain("note");
    }

    [Fact]
    public void Factory_Schema_KeepsEditStringsString_AndHidesInjectedParams()
    {
        var (registry, _) = WireCapturing(out _);
        var edit = new FileSystemToolFeature(registry.Object)
            .GetTools(new FeatureConfig())
            .Single(t => t.Name == "domain__filesystem__text_edit");

        var properties = edit.JsonSchema.GetProperty("properties");
        properties.TryGetProperty("arguments", out _).ShouldBeFalse();
        properties.TryGetProperty("cancellationToken", out _).ShouldBeFalse();

        var itemProps = properties.GetProperty("edits").GetProperty("items").GetProperty("properties");
        SchemaTypeAllows(itemProps.GetProperty("oldString"), "string").ShouldBeTrue();
        SchemaTypeAllows(itemProps.GetProperty("newString"), "string").ShouldBeTrue();
    }

    private static bool SchemaTypeAllows(JsonElement schema, string type)
    {
        var typeNode = schema.GetProperty("type");
        return typeNode.ValueKind == JsonValueKind.String
            ? typeNode.GetString() == type
            : typeNode.EnumerateArray().Any(t => t.GetString() == type);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsTextEditToolTests"`
Expected: FAIL to compile — `RunAsync` has no `arguments` parameter, and the factory does not coerce edits.

- [ ] **Step 3a: Update the tool body**

Replace `Domain/Tools/FileSystem/VfsTextEditTool.cs` with:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

public class VfsTextEditTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "edit";
    public const string Name = "text_edit";

    private const string CoercionNote =
        "One or more edits provided 'oldString'/'newString' as structured JSON rather than strings; their JSON text was used. Pass them as strings next time.";

    public const string ToolDescription = """
        Edits a text file by applying a non-empty list of edits in order, atomically.
        Each edit has oldString (exact, case-sensitive), newString, and replaceAll (default false).
        Edits are applied sequentially — edit N sees the result of edits 1…N-1.
        If any edit fails (oldString not found, or multiple matches without replaceAll), the file is not written.
        When replaceAll is false, oldString must appear exactly once at that point in the sequence.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to file (e.g., /library/notes/todo.md)")]
        string filePath,
        [Description("Edits to apply in order, atomically. Must be non-empty.")]
        IReadOnlyList<TextEdit> edits,
        AIFunctionArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(filePath);
        var result = await resolution.Backend.EditAsync(resolution.RelativePath, edits, cancellationToken);

        if (TextArg.EditsWereCoercedArg(arguments) && result.TryGetValue(out var value, out _))
        {
            return FsResultContract.ToNode(value with { Note = CoercionNote });
        }

        return result.ToNode();
    }
}
```

- [ ] **Step 3b: Wire the lenient binder**

In `Domain/Tools/FileSystem/FileSystemToolFeature.cs`, replace the `text_edit` tuple entry (currently line 28) with:

```csharp
            (VfsTextEditTool.Key, () => AIFunctionFactory.Create(
                new VfsTextEditTool(registry).RunAsync,
                new AIFunctionFactoryOptions
                {
                    Name = $"domain__{Feature}__{VfsTextEditTool.Name}",
                    ConfigureParameterBinding = parameter => parameter.Name == "edits"
                        ? new AIFunctionFactoryOptions.ParameterBindingOptions
                        {
                            BindParameter = (_, args) =>
                                TextArg.CoerceEdits(args.TryGetValue("edits", out var raw) ? raw : null)
                        }
                        : default
                })),
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsTextEditToolTests"`
Expected: PASS (all three).

Then run the whole filesystem-tool unit surface to confirm no regression:
Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Domain.Tools.FileSystem"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/FileSystem/VfsTextEditTool.cs Domain/Tools/FileSystem/FileSystemToolFeature.cs Tests/Unit/Domain/Tools/FileSystem/VfsTextEditToolTests.cs
git commit -m "feat(filesystem): text_edit coerces object oldString/newString to JSON text, notes it

Claude-Session: https://claude.ai/code/session_01SeYwcPTCgGLGzoXte3dgfo"
```

---

### Task 5: Full-suite regression sweep

**Files:** none (verification only).

- [ ] **Step 1: Build the solution**

Run: `dotnet build agent.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Run the whole unit test suite**

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E&Category!=Integration"`
Expected: PASS. Pay attention to `FsResultContractTests`, `FileSystemToolFeatureTests`, `VfsPromptToolNameConsistencyTests`, and the new `TextArg`/`VfsText*ToolTests`.

- [ ] **Step 3: (If Docker services are up) run the filesystem integration tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpAgentFileSystemTests"`
Expected: PASS — real `text_edit`/`text_create` flows still work with normal string arguments (guards that taking over `edits` binding did not change the happy path). If Docker is not up, note it as skipped rather than failed.

- [ ] **Step 4: Confirm formatting is clean**

Run: `dotnet format agent.sln --verify-no-changes --include Domain/Tools/FileSystem/ Domain/DTOs/FileSystem/`
Expected: no changes (top-level `Program.cs` files are a known permanent exception and are not in scope here).

No commit — this task only verifies.

---

## Self-Review

**1. Spec coverage:**
- Coerce object/array/number/bool/null → text for content/oldString/newString → Task 1 (`TextArg`), wired in Tasks 3–4. ✓
- Succeed + surface a note on coercion → Task 2 (`Note` member) + Tasks 3–4 (attach note). ✓
- Identical across every mount → coercion + note live in the tool bodies above `registry.Resolve`; Tasks 3–4 exercise via the feature/factory path that every mount shares. ✓
- Advertised schema stays `"string"` → param types unchanged; Tasks 3–4 include schema-pin tests asserting `content`/`oldString`/`newString` stay string and injected params are hidden. ✓
- `filePath` stays strict → only `content`/`edits` get a custom binder; Task 3/4 wiring returns `default` for every other parameter. ✓
- Domain `TextEdit` and backends untouched → confirmed; `CoerceEdits` maps to the existing `TextEdit`. ✓
- No new packages / env / config → all types already referenced by Domain. ✓

**2. Placeholder scan:** No TBD/TODO/"handle edge cases"/"similar to". Every code step has complete code. ✓

**3. Type consistency:** `TextArg.Coerce`/`WasCoerced`/`WasCoercedArg`/`CoerceEdits`/`EditsWereCoercedArg` names match between Task 1 definitions and Tasks 3–4 call sites. `FsCreateResult.Note`/`FsEditResult.Note` (`string?`) match between Task 2 and Tasks 3–4. `RunAsync` signatures match between the tool edits and the tests. `AIFunctionFactoryOptions.ParameterBindingOptions.BindParameter` (`Func<ParameterInfo, AIFunctionArguments, object?>`) matches the verified API. ✓
