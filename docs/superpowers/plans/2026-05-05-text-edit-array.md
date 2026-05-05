# Text Edit Array Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the single-edit signature on the agent's text edit tool with a non-empty array of edits applied sequentially against one file, all-or-nothing.

**Architecture:** A new `TextEdit` record DTO carries the per-edit fields. `VfsTextEditTool`, `IFileSystemBackend.EditAsync`, `TextEditTool.Run`, and the two `FsEditTool` MCP wrappers all swap their `(oldString, newString, replaceAll)` triple for `IReadOnlyList<TextEdit> edits`. `TextEditTool.Run` reads the file once, applies edits in order against the in-memory string, and writes a single time at the end via temp+rename — atomicity falls out of in-memory accumulation.

**Tech Stack:** .NET 10, xUnit, Shouldly, Moq, ModelContextProtocol SDK, `Microsoft.Extensions.AI` (`AIFunctionFactory`).

---

## Reference: Spec

`docs/superpowers/specs/2026-05-05-text-edit-array-design.md`

## Reference: Call chain (read these before starting)

- `Domain/Tools/FileSystem/VfsTextEditTool.cs` — LLM-facing tool
- `Domain/Contracts/IFileSystemBackend.cs` — backend contract
- `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs:35-44` — MCP client side
- `Domain/Tools/Text/TextEditTool.cs` — actual file I/O (used by both MCP servers)
- `McpServerVault/McpTools/FsEditTool.cs` and `McpServerSandbox/McpTools/FsEditTool.cs` — MCP server wrappers

## Conventions in this codebase

- Domain layer: no Infrastructure dependencies, file-scoped namespaces, primary constructors, `record` types for DTOs (see `.claude/rules/dotnet-style.md` and `.claude/rules/domain-layer.md`).
- MCP tools: no try/catch in tool methods; the global filter handles exceptions (`.claude/rules/mcp-tools.md`).
- TDD is enforced: write the failing test first, see it fail, then implement (`.claude/rules/tdd.md`).
- Memory note: worktrees should be created before subagent-driven dev. Caller of this plan is responsible for spinning up a worktree if using subagents.

---

## Task 1: Add `TextEdit` DTO

**Files:**
- Create: `Domain/DTOs/TextEdit.cs`

- [ ] **Step 1: Create the DTO**

Write `Domain/DTOs/TextEdit.cs`:

```csharp
using System.ComponentModel;

namespace Domain.DTOs;

public record TextEdit(
    [property: Description("Exact text to find (case-sensitive)")]
    string OldString,
    [property: Description("Replacement text")]
    string NewString,
    [property: Description("Replace all occurrences (default: false)")]
    bool ReplaceAll = false);
```

- [ ] **Step 2: Build the Domain project to confirm it compiles**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Domain/DTOs/TextEdit.cs
git commit -m "feat: add TextEdit DTO for batched edits"
```

---

## Task 2: Rewrite `TextEditTool.Run` to accept `IReadOnlyList<TextEdit>` (RED)

This is the heart of the change. We rewrite the unit tests first; they will fail to compile and run, which is the RED state.

**Files:**
- Modify: `Tests/Unit/Domain/Text/TextEditToolTests.cs`

- [ ] **Step 1: Replace the test file contents**

Overwrite `Tests/Unit/Domain/Text/TextEditToolTests.cs` with:

```csharp
using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.Tools.Text;
using Shouldly;

namespace Tests.Unit.Domain.Text;

public class TextEditToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableTextEditTool _tool;

    public TextEditToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"text-edit-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableTextEditTool(_testDir, [".md", ".txt"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void Run_SingleEdit_ReplacesText()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var result = _tool.TestRun(filePath, [new TextEdit("World", "Universe")]);

        result["status"]!.ToString().ShouldBe("success");
        result["totalOccurrencesReplaced"]!.GetValue<int>().ShouldBe(1);
        result["edits"]!.AsArray().Count.ShouldBe(1);
        result["edits"]![0]!["occurrencesReplaced"]!.GetValue<int>().ShouldBe(1);
        File.ReadAllText(filePath).ShouldBe("Hello Universe");
    }

    [Fact]
    public void Run_MultipleOccurrences_ReplaceAllFalse_Throws()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, [new TextEdit("foo", "FOO")]));
        ex.Message.ShouldContain("3 occurrences");
        ex.Message.ShouldContain("disambiguate");
        File.ReadAllText(filePath).ShouldBe("foo bar foo baz foo");
    }

    [Fact]
    public void Run_MultipleOccurrences_ReplaceAllTrue_ReplacesAll()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var result = _tool.TestRun(filePath, [new TextEdit("foo", "FOO", ReplaceAll: true)]);

        result["status"]!.ToString().ShouldBe("success");
        result["totalOccurrencesReplaced"]!.GetValue<int>().ShouldBe(3);
        File.ReadAllText(filePath).ShouldBe("FOO bar FOO baz FOO");
    }

    [Fact]
    public void Run_NotFound_Throws()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, [new TextEdit("Missing", "X")]));
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public void Run_CaseInsensitiveMatch_ThrowsWithSuggestion()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, [new TextEdit("hello world", "X")]));
        ex.Message.ShouldContain("Did you mean");
    }

    [Fact]
    public void Run_MultilineOldString_ReplacesAcrossLines()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nLine 3\nLine 4");

        var result = _tool.TestRun(filePath, [new TextEdit("Line 2\nLine 3", "Replacement")]);

        result["status"]!.ToString().ShouldBe("success");
        File.ReadAllText(filePath).ShouldBe("Line 1\nReplacement\nLine 4");
    }

    [Fact]
    public void Run_ReturnsAffectedLinesPerEdit()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nTarget\nLine 4");

        var result = _tool.TestRun(filePath, [new TextEdit("Target", "Replaced")]);

        result["edits"]![0]!["affectedLines"]!["start"]!.GetValue<int>().ShouldBe(3);
        result["edits"]![0]!["affectedLines"]!["end"]!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public void Run_AtomicWrite_NoTmpFileRemains()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        _tool.TestRun(filePath, [new TextEdit("World", "Universe")]);

        File.Exists(filePath + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public void Run_MultipleEdits_AppliedInOrder()
    {
        var filePath = CreateTestFile("test.txt", "alpha beta gamma");

        var result = _tool.TestRun(filePath,
        [
            new TextEdit("alpha", "ALPHA"),
            new TextEdit("beta", "BETA"),
            new TextEdit("gamma", "GAMMA")
        ]);

        result["status"]!.ToString().ShouldBe("success");
        result["totalOccurrencesReplaced"]!.GetValue<int>().ShouldBe(3);
        result["edits"]!.AsArray().Count.ShouldBe(3);
        File.ReadAllText(filePath).ShouldBe("ALPHA BETA GAMMA");
    }

    [Fact]
    public void Run_LaterEdit_CanMatchTextProducedByEarlierEdit()
    {
        var filePath = CreateTestFile("test.txt", "one");

        var result = _tool.TestRun(filePath,
        [
            new TextEdit("one", "two"),
            new TextEdit("two", "three")
        ]);

        result["totalOccurrencesReplaced"]!.GetValue<int>().ShouldBe(2);
        File.ReadAllText(filePath).ShouldBe("three");
    }

    [Fact]
    public void Run_MidSequenceFailure_FileUnchanged()
    {
        var filePath = CreateTestFile("test.txt", "alpha beta");
        var originalContent = File.ReadAllText(filePath);

        Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath,
            [
                new TextEdit("alpha", "ALPHA"),
                new TextEdit("does-not-exist", "X"),
                new TextEdit("beta", "BETA")
            ]));

        File.ReadAllText(filePath).ShouldBe(originalContent);
        File.Exists(filePath + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public void Run_EmptyEditsArray_Throws()
    {
        var filePath = CreateTestFile("test.txt", "content");

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, []));
        ex.Message.ShouldContain("edits");
    }

    [Fact]
    public void Run_TotalOccurrencesIsSumOfPerEditCounts()
    {
        var filePath = CreateTestFile("test.txt", "a a a b b");

        var result = _tool.TestRun(filePath,
        [
            new TextEdit("a", "A", ReplaceAll: true),
            new TextEdit("b", "B", ReplaceAll: true)
        ]);

        result["totalOccurrencesReplaced"]!.GetValue<int>().ShouldBe(5);
        result["edits"]![0]!["occurrencesReplaced"]!.GetValue<int>().ShouldBe(3);
        result["edits"]![1]!["occurrencesReplaced"]!.GetValue<int>().ShouldBe(2);
    }

    private string CreateTestFile(string name, string content)
    {
        var path = Path.Combine(_testDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private class TestableTextEditTool(string vaultPath, string[] allowedExtensions)
        : TextEditTool(vaultPath, allowedExtensions)
    {
        public JsonNode TestRun(string filePath, IReadOnlyList<TextEdit> edits)
        {
            return Run(filePath, edits);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet build Tests/Tests.csproj`
Expected: Build FAILS — `TextEditTool.Run` does not accept `IReadOnlyList<TextEdit>`, and existing call sites pass scalar args. The compile errors are evidence of RED.

- [ ] **Step 3: Do not commit yet** — RED is incomplete until production code compiles. Move to Task 3.

---

## Task 3: Implement the new `TextEditTool.Run` (GREEN, part 1)

**Files:**
- Modify: `Domain/Tools/Text/TextEditTool.cs`

- [ ] **Step 1: Replace the file contents**

Overwrite `Domain/Tools/Text/TextEditTool.cs` with:

```csharp
using System.Text.Json.Nodes;
using Domain.DTOs;

namespace Domain.Tools.Text;

public class TextEditTool(string vaultPath, string[] allowedExtensions)
    : TextToolBase(vaultPath, allowedExtensions)
{
    protected const string Description = """
                                         Edits a text file by applying a non-empty list of edits in order, atomically.

                                         Parameters:
                                         - filePath: Path to file (absolute or relative to vault)
                                         - edits: Ordered list of edits. Each edit has:
                                           - oldString: Exact text to find (case-sensitive)
                                           - newString: Replacement text
                                           - replaceAll: Replace all occurrences (default: false)

                                         Edits are applied sequentially against the running file contents — edit N sees the result of edits 1…N-1.
                                         If any edit fails (oldString not found, or multiple matches without replaceAll), the file is not written.

                                         When replaceAll is false, oldString must appear exactly once at that point in the sequence.
                                         If multiple occurrences are found, the tool fails — provide more surrounding context in oldString to disambiguate.

                                         Insert: include surrounding context in oldString, add new lines in newString.
                                         Delete: include content in oldString, omit it from newString.
                                         """;

    protected JsonNode Run(string filePath, IReadOnlyList<TextEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
        {
            throw new ArgumentException("edits must contain at least one entry.", nameof(edits));
        }

        var fullPath = ValidateAndResolvePath(filePath);
        var content = File.ReadAllText(fullPath);

        var perEditResults = new JsonArray();
        var totalReplaced = 0;

        foreach (var edit in edits)
        {
            var positions = FindAllOccurrences(content, edit.OldString);

            if (positions.Count == 0)
            {
                var suggestion = FindCaseInsensitiveSuggestion(content, edit.OldString);
                if (suggestion is not null)
                {
                    throw new ArgumentException(
                        $"Text '{Truncate(edit.OldString, 100)}' not found (case-sensitive). Did you mean '{Truncate(suggestion, 100)}'?");
                }

                throw new ArgumentException($"Text '{Truncate(edit.OldString, 100)}' not found in file.");
            }

            if (!edit.ReplaceAll && positions.Count > 1)
            {
                throw new ArgumentException(
                    $"Found {positions.Count} occurrences of the specified text. Provide more surrounding context in oldString to disambiguate, or set replaceAll=true.");
            }

            var firstPosition = positions[0];
            content = edit.ReplaceAll
                ? content.Replace(edit.OldString, edit.NewString, StringComparison.Ordinal)
                : ReplaceFirst(content, edit.OldString, edit.NewString, firstPosition);

            var replacedCount = edit.ReplaceAll ? positions.Count : 1;
            totalReplaced += replacedCount;

            var (startLine, endLine) = ComputeAffectedLines(content, firstPosition, edit.NewString.Length);

            perEditResults.Add(new JsonObject
            {
                ["occurrencesReplaced"] = replacedCount,
                ["affectedLines"] = new JsonObject
                {
                    ["start"] = startLine,
                    ["end"] = endLine
                }
            });
        }

        var tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, fullPath, overwrite: true);

        return new JsonObject
        {
            ["status"] = "success",
            ["filePath"] = fullPath,
            ["totalOccurrencesReplaced"] = totalReplaced,
            ["edits"] = perEditResults
        };
    }

    private static string ReplaceFirst(string content, string oldString, string newString, int position)
    {
        return content[..position] + newString + content[(position + oldString.Length)..];
    }

    private static List<int> FindAllOccurrences(string content, string searchText)
    {
        var positions = new List<int>();
        var index = 0;

        while ((index = content.IndexOf(searchText, index, StringComparison.Ordinal)) >= 0)
        {
            positions.Add(index);
            index += searchText.Length;
        }

        return positions;
    }

    private static string? FindCaseInsensitiveSuggestion(string content, string searchText)
    {
        var index = content.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? content.Substring(index, searchText.Length) : null;
    }

    private static (int StartLine, int EndLine) ComputeAffectedLines(string content, int position, int newLength)
    {
        var startLine = content[..position].Count(c => c == '\n') + 1;
        var newTextContent = content.Substring(position, newLength);
        var linesInNew = newTextContent.Count(c => c == '\n');
        return (startLine, startLine + linesInNew);
    }

    private static string Truncate(string text, int maxLength)
    {
        return text.Length > maxLength ? text[..maxLength] + "..." : text;
    }
}
```

Notes for the implementer:
- `affectedLines` is computed against the **post-edit** content using the **new** string's length and the position where the match was. For `replaceAll`, we report the position of the first occurrence.
- Atomicity comes for free: we only call `File.WriteAllText` after every edit has succeeded.
- The unused-import warning compiler will flag if `using System.Linq` is missing — `Count(c => c == '\n')` requires it; it's already implicitly included via global usings in this project, but if a build fails on it, add `using System.Linq;`.

- [ ] **Step 2: Build Domain to confirm it compiles**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Do not commit yet — production code still has stale callers (`TextEditTool` is consumed by `FsEditTool`).** Move to Task 4.

---

## Task 4: Update both `FsEditTool` MCP wrappers (GREEN, part 2)

**Files:**
- Modify: `McpServerVault/McpTools/FsEditTool.cs`
- Modify: `McpServerSandbox/McpTools/FsEditTool.cs`

- [ ] **Step 1: Update `McpServerVault/McpTools/FsEditTool.cs`**

Overwrite with:

```csharp
using System.ComponentModel;
using Domain.DTOs;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerVault.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerVault.McpTools;

[McpServerToolType]
public class FsEditTool(McpSettings settings)
    : TextEditTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = "fs_edit")]
    [Description(Description)]
    public CallToolResult McpRun(
        string path,
        IReadOnlyList<TextEdit> edits)
    {
        return ToolResponse.Create(Run(path, edits));
    }
}
```

- [ ] **Step 2: Update `McpServerSandbox/McpTools/FsEditTool.cs`**

Overwrite with:

```csharp
using System.ComponentModel;
using Domain.DTOs;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerSandbox.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsEditTool(McpSettings settings)
    : TextEditTool(settings.ContainerRoot, settings.AllowedExtensions)
{
    [McpServerTool(Name = "fs_edit")]
    [Description(Description)]
    public CallToolResult McpRun(
        string path,
        IReadOnlyList<TextEdit> edits)
    {
        return ToolResponse.Create(Run(path, edits));
    }
}
```

- [ ] **Step 3: Build both server projects**

Run: `dotnet build McpServerVault/McpServerVault.csproj McpServerSandbox/McpServerSandbox.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Do not commit yet — Domain backend contract and `VfsTextEditTool` still need updating.** Move to Task 5.

---

## Task 5: Update `IFileSystemBackend.EditAsync` and `McpFileSystemBackend.EditAsync`

**Files:**
- Modify: `Domain/Contracts/IFileSystemBackend.cs:13`
- Modify: `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs:35-44`

- [ ] **Step 1: Edit `Domain/Contracts/IFileSystemBackend.cs`**

Replace line 13:

```csharp
Task<JsonNode> EditAsync(string path, string oldString, string newString, bool replaceAll, CancellationToken ct);
```

with:

```csharp
Task<JsonNode> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct);
```

Add `using Domain.DTOs;` to the file's usings if it's not already imported by another DTO.

- [ ] **Step 2: Edit `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`**

Replace the existing `EditAsync` method (lines 35-44):

```csharp
public async Task<JsonNode> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct)
{
    return await CallToolAsync("fs_edit", new Dictionary<string, object?>
    {
        ["path"] = path,
        ["edits"] = edits.Select(e => new Dictionary<string, object?>
        {
            ["oldString"] = e.OldString,
            ["newString"] = e.NewString,
            ["replaceAll"] = e.ReplaceAll
        }).ToList()
    }, ct);
}
```

Add `using Domain.DTOs;` if not already present.

- [ ] **Step 3: Build the affected projects**

Run: `dotnet build Domain/Domain.csproj Infrastructure/Infrastructure.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Do not commit yet.** Move to Task 6.

---

## Task 6: Update `VfsTextEditTool` to expose the array signature to the LLM

**Files:**
- Modify: `Domain/Tools/FileSystem/VfsTextEditTool.cs`

- [ ] **Step 1: Replace the file contents**

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.FileSystem;

public class VfsTextEditTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "edit";
    public const string Name = "text_edit";

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
        CancellationToken cancellationToken = default)
    {
        return await registry.Resolve(filePath).Backend
            .EditAsync(registry.Resolve(filePath).RelativePath, edits, cancellationToken);
    }
}
```

Wait — that resolves twice. Use a local instead:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.FileSystem;

public class VfsTextEditTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "edit";
    public const string Name = "text_edit";

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
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(filePath);
        return await resolution.Backend.EditAsync(resolution.RelativePath, edits, cancellationToken);
    }
}
```

- [ ] **Step 2: Build the Domain project**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded, 0 errors.

---

## Task 7: Rewrite `VfsTextEditToolTests` to assert array dispatch (RED + GREEN combined since the production change already exists)

**Files:**
- Modify: `Tests/Unit/Domain/Tools/FileSystem/VfsTextEditToolTests.cs`

- [ ] **Step 1: Replace the test file contents**

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
    private readonly VfsTextEditTool _tool;

    public TextEditToolTests()
    {
        _tool = new VfsTextEditTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndForwardsEdits()
    {
        var expected = new JsonObject { ["status"] = "success", ["totalOccurrencesReplaced"] = 1 };
        var edits = new[] { new TextEdit("old", "new") };
        _registry.Setup(r => r.Resolve("/library/file.md"))
            .Returns(new FileSystemResolution(_backend.Object, "file.md"));
        _backend.Setup(b => b.EditAsync(
                "file.md",
                It.Is<IReadOnlyList<TextEdit>>(list =>
                    list.Count == 1 &&
                    list[0].OldString == "old" &&
                    list[0].NewString == "new" &&
                    list[0].ReplaceAll == false),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/library/file.md", edits, CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_ForwardsMultipleEditsInOrder()
    {
        var expected = new JsonObject { ["status"] = "success", ["totalOccurrencesReplaced"] = 5 };
        var edits = new[]
        {
            new TextEdit("a", "A", ReplaceAll: true),
            new TextEdit("b", "B")
        };
        _registry.Setup(r => r.Resolve("/vault/config.md"))
            .Returns(new FileSystemResolution(_backend.Object, "config.md"));
        _backend.Setup(b => b.EditAsync(
                "config.md",
                It.Is<IReadOnlyList<TextEdit>>(list =>
                    list.Count == 2 &&
                    list[0].OldString == "a" && list[0].ReplaceAll == true &&
                    list[1].OldString == "b" && list[1].ReplaceAll == false),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/vault/config.md", edits, CancellationToken.None);

        result.ShouldBe(expected);
    }
}
```

- [ ] **Step 2: Build the Tests project**

Run: `dotnet build Tests/Tests.csproj`
Expected: Build succeeded, 0 errors. (Both test files now compile.)

---

## Task 8: Run all unit tests — full GREEN

- [ ] **Step 1: Run the unit tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit"`
Expected: All tests pass. Specifically `Tests.Unit.Domain.Text.TextEditToolTests` (12 tests) and `Tests.Unit.Domain.Tools.FileSystem.TextEditToolTests` (2 tests) all pass.

- [ ] **Step 2: If any test fails, fix the smallest thing that makes it pass and re-run.** Common likely failures and fixes:
  - `Run_ReturnsAffectedLinesPerEdit` — verify `ComputeAffectedLines` uses `newString.Length` (we replaced the parameter from `oldLength` to `newLength`).
  - Empty-array test — confirm the `ArgumentException` message contains the literal substring `edits` (it does in the implementation above, via `nameof(edits)`).

---

## Task 9: Run integration tests for the MCP servers

- [ ] **Step 1: Run integration tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Integration.McpServerTests"`
Expected: All tests pass. The Vault test at `Tests/Integration/McpServerTests/McpVaultServerTests.cs:82` (`toolNames.ShouldContain("fs_edit")`) still passes — we didn't rename the tool.

- [ ] **Step 2: If integration tests skip due to environment (Docker, Redis, etc.), record that and continue.** No code change should be needed in integration tests for this work.

---

## Task 10: Build the entire solution to catch any consumer we missed

- [ ] **Step 1: Build the solution**

Run: `dotnet build`
Expected: Build succeeded, 0 errors. If any project fails because it called the old 4-arg `EditAsync` or `Run`, fix that call site by wrapping its args in a `[new TextEdit(old, new, replaceAll)]` list.

- [ ] **Step 2: Search for any stragglers we may have missed**

Run: `grep -rn "EditAsync\|TextEditTool" --include="*.cs" .`
Expected: Every match either takes `IReadOnlyList<TextEdit>` or is a comment/test reference. No 4-arg call remains.

---

## Task 11: Final commit

- [ ] **Step 1: Stage and commit everything**

```bash
git add Domain/Tools/Text/TextEditTool.cs \
        Domain/Tools/FileSystem/VfsTextEditTool.cs \
        Domain/Contracts/IFileSystemBackend.cs \
        Infrastructure/Agents/Mcp/McpFileSystemBackend.cs \
        McpServerVault/McpTools/FsEditTool.cs \
        McpServerSandbox/McpTools/FsEditTool.cs \
        Tests/Unit/Domain/Text/TextEditToolTests.cs \
        Tests/Unit/Domain/Tools/FileSystem/VfsTextEditToolTests.cs

git commit -m "$(cat <<'EOF'
feat: text edit accepts an ordered array of edits

VfsTextEditTool, IFileSystemBackend.EditAsync, TextEditTool.Run, and
both FsEditTool MCP wrappers now take IReadOnlyList<TextEdit> and apply
edits sequentially against in-memory content, writing once at the end.
Atomic on failure.
EOF
)"
```

- [ ] **Step 2: Confirm the commit**

Run: `git log -1 --stat`
Expected: One commit listing the 8 files above.

---

## Self-Review Notes

Spec coverage:
- DTO: Task 1 ✓
- `VfsTextEditTool` array signature: Task 6 ✓
- `IFileSystemBackend.EditAsync` array signature: Task 5 ✓
- `McpFileSystemBackend` serialization: Task 5 ✓
- `TextEditTool.Run` sequential atomic implementation: Task 3 ✓
- Both `FsEditTool` wrappers: Task 4 ✓
- Result shape (`status`, `filePath`, `totalOccurrencesReplaced`, `edits[]`): Task 3 ✓
- Empty-array rejection: Task 2 (test) + Task 3 (impl) ✓
- Mid-sequence failure leaves file unchanged: Task 2 (test) + Task 3 (impl) ✓
- Sequential semantics (later edit sees earlier output): Task 2 (test) ✓
- LLM-facing tool name unchanged (`text_edit`/`fs_edit`): Tasks 4, 6 ✓
- Backend dispatch test: Task 7 ✓
