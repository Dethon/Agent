# GlobFiles Mode Parameter Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a `mode` parameter (directories/files) to the GlobFiles tool with a 200-result cap on file mode to prevent token waste.

**Architecture:** The `GlobFilesTool` base class gains a `mode` parameter defaulting to `Directories`. Directory mode calls a new `GlobDirectories` on `IFileSystemClient`. File mode uses existing `GlobFiles` but truncates at 200 results. MCP wrappers pass the mode through. Agent prompts are updated to guide incremental exploration.

**Tech Stack:** .NET 10, xUnit, Shouldly, Moq, Microsoft.Extensions.FileSystemGlobbing

---

### Task 1: Add GlobMode Enum and GlobDirectories to Domain Contract

**Files:**
- Create: `Domain/Tools/Files/GlobMode.cs`
- Modify: `Domain/Contracts/IFileSystemClient.cs`

**Step 1: Create enum file**

Create `Domain/Tools/Files/GlobMode.cs`:

```csharp
namespace Domain.Tools.Files;

public enum GlobMode
{
    Directories,
    Files
}
```

**Step 2: Add GlobDirectories to IFileSystemClient**

In `Domain/Contracts/IFileSystemClient.cs`, add after line 6 (`GlobFiles`):

```csharp
Task<string[]> GlobDirectories(string basePath, string pattern, CancellationToken cancellationToken = default);
```

**Step 3: Verify it compiles**

Run: `dotnet build Domain`
Expected: SUCCESS (it's just an interface and enum, no consumers yet need updating)

Run: `dotnet build Infrastructure`
Expected: FAIL — `LocalFileSystemClient` doesn't implement `GlobDirectories` yet. This is expected; we'll fix in Task 3.

**Step 4: Commit**

```bash
git add Domain/Tools/Files/GlobMode.cs Domain/Contracts/IFileSystemClient.cs
git commit -m "feat: add GlobMode enum and GlobDirectories contract"
```

---

### Task 2: Add Mode Parameter to GlobFilesTool (TDD)

**Files:**
- Modify: `Tests/Unit/Tools/GlobFilesToolTests.cs`
- Modify: `Domain/Tools/Files/GlobFilesTool.cs`

**Step 1: Write failing tests for directory mode**

Add these tests to `GlobFilesToolTests.cs`. First update the `TestableGlobFilesTool` wrapper (at the bottom of the file) to expose the new `Run` overload:

Replace the existing `TestableGlobFilesTool` class with:

```csharp
private class TestableGlobFilesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
    : GlobFilesTool(client, libraryPath)
{
    public Task<JsonNode> TestRun(string pattern, CancellationToken cancellationToken)
        => Run(pattern, GlobMode.Files, cancellationToken);

    public Task<JsonNode> TestRun(string pattern, GlobMode mode, CancellationToken cancellationToken)
        => Run(pattern, mode, cancellationToken);
}
```

Then add these test methods:

```csharp
[Fact]
public async Task Run_DirectoriesMode_CallsGlobDirectoriesAndReturnsArray()
{
    // Arrange
    _mockClient.Setup(c => c.GlobDirectories(BasePath, "**/*", It.IsAny<CancellationToken>()))
        .ReturnsAsync(["/library/movies", "/library/books"]);

    // Act
    var result = await _tool.TestRun("**/*", GlobMode.Directories, CancellationToken.None);

    // Assert
    var array = result.AsArray();
    array.Count.ShouldBe(2);
    array[0]!.GetValue<string>().ShouldBe("/library/movies");
}

[Fact]
public async Task Run_FilesMode_UnderCap_ReturnsPlainArray()
{
    // Arrange
    _mockClient.Setup(c => c.GlobFiles(BasePath, "**/*.pdf", It.IsAny<CancellationToken>()))
        .ReturnsAsync(["/library/a.pdf", "/library/b.pdf"]);

    // Act
    var result = await _tool.TestRun("**/*.pdf", GlobMode.Files, CancellationToken.None);

    // Assert
    var array = result.AsArray();
    array.Count.ShouldBe(2);
}

[Fact]
public async Task Run_FilesMode_OverCap_ReturnsTruncatedObject()
{
    // Arrange
    var files = Enumerable.Range(1, 250).Select(i => $"/library/file{i}.pdf").ToArray();
    _mockClient.Setup(c => c.GlobFiles(BasePath, "**/*.pdf", It.IsAny<CancellationToken>()))
        .ReturnsAsync(files);

    // Act
    var result = await _tool.TestRun("**/*.pdf", GlobMode.Files, CancellationToken.None);

    // Assert
    var obj = result.AsObject();
    obj["truncated"]!.GetValue<bool>().ShouldBeTrue();
    obj["total"]!.GetValue<int>().ShouldBe(250);
    obj["files"]!.AsArray().Count.ShouldBe(200);
    obj["message"]!.GetValue<string>().ShouldContain("200");
    obj["message"]!.GetValue<string>().ShouldContain("250");
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test Tests --filter "GlobFilesToolTests" -v minimal`
Expected: FAIL — `Run` method doesn't accept `GlobMode` parameter yet.

**Step 3: Implement mode support in GlobFilesTool**

Replace the entire `Domain/Tools/Files/GlobFilesTool.cs` with:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools.Files;

public class GlobFilesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    protected const string Name = "GlobFiles";

    protected const string Description = """
                                         Searches for files or directories matching a glob pattern relative to the library root.
                                         Supports * (single segment), ** (recursive), and ? (single char).
                                         Use mode 'directories' (default) to explore the library structure first, then 'files' with specific patterns to find content.
                                         In files mode, results are capped at 200—use more specific patterns if truncated.
                                         """;

    private const int FileResultCap = 200;

    protected async Task<JsonNode> Run(string pattern, GlobMode mode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        if (pattern.Contains(".."))
        {
            throw new ArgumentException("Pattern must not contain '..' segments", nameof(pattern));
        }

        return mode switch
        {
            GlobMode.Directories => await RunDirectories(pattern, cancellationToken),
            GlobMode.Files => await RunFiles(pattern, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Invalid glob mode")
        };
    }

    private async Task<JsonNode> RunDirectories(string pattern, CancellationToken cancellationToken)
    {
        var result = await client.GlobDirectories(libraryPath.BaseLibraryPath, pattern, cancellationToken);
        return JsonSerializer.SerializeToNode(result)
               ?? throw new InvalidOperationException("Failed to serialize GlobDirectories result");
    }

    private async Task<JsonNode> RunFiles(string pattern, CancellationToken cancellationToken)
    {
        var result = await client.GlobFiles(libraryPath.BaseLibraryPath, pattern, cancellationToken);

        if (result.Length <= FileResultCap)
        {
            return JsonSerializer.SerializeToNode(result)
                   ?? throw new InvalidOperationException("Failed to serialize GlobFiles result");
        }

        var truncated = new JsonObject
        {
            ["files"] = JsonSerializer.SerializeToNode(result.Take(FileResultCap).ToArray()),
            ["truncated"] = true,
            ["total"] = result.Length,
            ["message"] = $"Showing {FileResultCap} of {result.Length} matches. Use a more specific pattern to narrow results."
        };
        return truncated;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test Tests --filter "GlobFilesToolTests" -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add Domain/Tools/Files/GlobFilesTool.cs Tests/Unit/Tools/GlobFilesToolTests.cs
git commit -m "feat: add mode parameter to GlobFilesTool with file result cap"
```

---

### Task 3: Implement GlobDirectories in LocalFileSystemClient (TDD)

**Files:**
- Modify: `Tests/Integration/Infrastructure/LocalFileSystemClientTests.cs`
- Modify: `Infrastructure/Clients/LocalFileSystemClient.cs`

**Step 1: Write failing integration tests**

Add these tests to `LocalFileSystemClientTests.cs`:

```csharp
[Fact]
public async Task GlobDirectories_WithRecursivePattern_ReturnsDistinctDirectories()
{
    // Arrange
    var moviesDir = Path.Combine(_testDir, "movies");
    var booksDir = Path.Combine(_testDir, "books");
    var subDir = Path.Combine(moviesDir, "action");
    Directory.CreateDirectory(subDir);
    Directory.CreateDirectory(booksDir);
    await File.WriteAllTextAsync(Path.Combine(subDir, "film.mkv"), "content");
    await File.WriteAllTextAsync(Path.Combine(booksDir, "book.pdf"), "content");
    await File.WriteAllTextAsync(Path.Combine(moviesDir, "other.mkv"), "content");

    // Act
    var dirs = await _client.GlobDirectories(_testDir, "**/*");

    // Assert
    dirs.ShouldContain(d => d.EndsWith("movies"));
    dirs.ShouldContain(d => d.EndsWith("action"));
    dirs.ShouldContain(d => d.EndsWith("books"));
    dirs.Distinct().Count().ShouldBe(dirs.Length);
}

[Fact]
public async Task GlobDirectories_WithSpecificPattern_ReturnsOnlyMatchingDirectories()
{
    // Arrange
    var moviesDir = Path.Combine(_testDir, "movies");
    var booksDir = Path.Combine(_testDir, "books");
    Directory.CreateDirectory(moviesDir);
    Directory.CreateDirectory(booksDir);
    await File.WriteAllTextAsync(Path.Combine(moviesDir, "film.mkv"), "content");
    await File.WriteAllTextAsync(Path.Combine(booksDir, "book.pdf"), "content");

    // Act
    var dirs = await _client.GlobDirectories(_testDir, "movies/**/*");

    // Assert
    dirs.Length.ShouldBe(1);
    dirs[0].ShouldEndWith("movies");
}

[Fact]
public async Task GlobDirectories_WithNoMatches_ReturnsEmptyArray()
{
    // Arrange
    await File.WriteAllTextAsync(Path.Combine(_testDir, "file.txt"), "content");

    // Act
    var dirs = await _client.GlobDirectories(_testDir, "nonexistent/**/*");

    // Assert
    dirs.ShouldBeEmpty();
}

[Fact]
public async Task GlobDirectories_ReturnsAbsolutePaths()
{
    // Arrange
    var subDir = Path.Combine(_testDir, "sub");
    Directory.CreateDirectory(subDir);
    await File.WriteAllTextAsync(Path.Combine(subDir, "file.txt"), "content");

    // Act
    var dirs = await _client.GlobDirectories(_testDir, "**/*");

    // Assert
    dirs.ShouldAllBe(d => d.StartsWith(_testDir));
}

[Fact]
public async Task GlobDirectories_EmptyDirectory_ReturnsEmptyArray()
{
    // Arrange
    Directory.CreateDirectory(Path.Combine(_testDir, "empty"));

    // Act
    var dirs = await _client.GlobDirectories(_testDir, "**/*");

    // Assert
    dirs.ShouldBeEmpty();
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test Tests --filter "LocalFileSystemClientTests.GlobDirectories" -v minimal`
Expected: FAIL — `GlobDirectories` method doesn't exist on `LocalFileSystemClient` yet.

**Step 3: Implement GlobDirectories**

In `Infrastructure/Clients/LocalFileSystemClient.cs`, add after the `GlobFiles` method (after line 24):

```csharp
public Task<string[]> GlobDirectories(string basePath, string pattern, CancellationToken cancellationToken = default)
{
    var matcher = new Matcher();
    matcher.AddInclude(pattern);
    var result = matcher.GetResultsInFullPath(basePath)
        .Select(f => Path.GetDirectoryName(f)!)
        .Where(d => d != basePath)
        .Distinct()
        .Order()
        .ToArray();
    return Task.FromResult(result);
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test Tests --filter "LocalFileSystemClientTests.GlobDirectories" -v minimal`
Expected: ALL PASS

**Step 5: Verify all existing tests still pass**

Run: `dotnet test Tests --filter "LocalFileSystemClientTests" -v minimal`
Expected: ALL PASS (no regressions)

**Step 6: Commit**

```bash
git add Infrastructure/Clients/LocalFileSystemClient.cs Tests/Integration/Infrastructure/LocalFileSystemClientTests.cs
git commit -m "feat: implement GlobDirectories in LocalFileSystemClient"
```

---

### Task 4: Update MCP Tool Wrappers

**Files:**
- Modify: `McpServerLibrary/McpTools/McpGlobFilesTool.cs`
- Modify: `McpServerText/McpTools/McpTextGlobFilesTool.cs`

**Step 1: Update McpGlobFilesTool**

Replace the entire `McpServerLibrary/McpTools/McpGlobFilesTool.cs` with:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class McpGlobFilesTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : GlobFilesTool(client, libraryPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("Glob pattern relative to the library root. Examples: **/*.pdf, books/*, **/*.mkv")]
        string pattern,
        [Description("Search mode: 'directories' (default) lists matching directories for exploration, 'files' lists matching files (capped at 200 results).")]
        string mode = "directories",
        CancellationToken cancellationToken = default)
    {
        var globMode = ParseMode(mode);
        return ToolResponse.Create(await Run(pattern, globMode, cancellationToken));
    }

    private static GlobMode ParseMode(string mode) =>
        mode.ToLowerInvariant() switch
        {
            "directories" => GlobMode.Directories,
            "files" => GlobMode.Files,
            _ => throw new ArgumentException($"Invalid mode '{mode}'. Use 'directories' or 'files'.", nameof(mode))
        };
}
```

**Step 2: Update McpTextGlobFilesTool**

Replace the entire `McpServerText/McpTools/McpTextGlobFilesTool.cs` with:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpTextGlobFilesTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : GlobFilesTool(client, libraryPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("Glob pattern relative to the library root. Examples: **/*.md, notes/*, **/*.txt")]
        string pattern,
        [Description("Search mode: 'directories' (default) lists matching directories for exploration, 'files' lists matching files (capped at 200 results).")]
        string mode = "directories",
        CancellationToken cancellationToken = default)
    {
        var globMode = ParseMode(mode);
        return ToolResponse.Create(await Run(pattern, globMode, cancellationToken));
    }

    private static GlobMode ParseMode(string mode) =>
        mode.ToLowerInvariant() switch
        {
            "directories" => GlobMode.Directories,
            "files" => GlobMode.Files,
            _ => throw new ArgumentException($"Invalid mode '{mode}'. Use 'directories' or 'files'.", nameof(mode))
        };
}
```

**Step 3: Verify it compiles**

Run: `dotnet build McpServerLibrary && dotnet build McpServerText`
Expected: SUCCESS

**Step 4: Run all tests**

Run: `dotnet test Tests -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add McpServerLibrary/McpTools/McpGlobFilesTool.cs McpServerText/McpTools/McpTextGlobFilesTool.cs
git commit -m "feat: add mode parameter to MCP glob tool wrappers"
```

---

### Task 5: Update Agent Prompts

**Files:**
- Modify: `Domain/Prompts/KnowledgeBasePrompt.cs`
- Modify: `Domain/Prompts/DownloaderPrompt.cs`

**Step 1: Update KnowledgeBasePrompt**

In `Domain/Prompts/KnowledgeBasePrompt.cs`, make these changes:

Replace lines 50-51 (the GlobFiles tool description in Available Tools):
```
        - `GlobFiles` - Search for files matching a glob pattern (e.g., **/*.md, notes/*)
```
with:
```
        - `GlobFiles` - Search for files or directories matching a glob pattern. Use mode='directories' (default) to explore structure, mode='files' for specific content (capped at 200 results)
```

Replace lines 72-73 (the Exploring the Vault section):
```
        1. Use GlobFiles to see the vault structure (e.g., **/* for all files, */ for top-level folders)
        2. Present an overview to help the user navigate
```
with:
```
        1. Use GlobFiles with directories mode to see the vault structure (e.g., **/* for all directories)
        2. Then use files mode with specific patterns to find content (e.g., notes/*.md)
        3. Present an overview to help the user navigate
```

**Step 2: Update DownloaderPrompt**

In `Domain/Prompts/DownloaderPrompt.cs`, make these changes:

Replace line 103 (the GlobFiles usage in Phase 3):
```
        1.  **Survey the Hoard:** Use the GlobFiles tool to understand how the user's current treasures are organized (e.g., `GlobFiles **/*` to see all files). **If you have already called GlobFiles in this conversation, reuse that cached result—do not call it again.**
```
with:
```
        1.  **Survey the Hoard:** Use the GlobFiles tool with directories mode to understand how the user's current treasures are organized (e.g., `GlobFiles **/*` to see all directories). Then use files mode with specific patterns to find content in target directories. **If you have already called GlobFiles in this conversation, reuse that cached result—do not call it again.**
```

**Step 3: Verify it compiles**

Run: `dotnet build Domain`
Expected: SUCCESS

**Step 4: Commit**

```bash
git add Domain/Prompts/KnowledgeBasePrompt.cs Domain/Prompts/DownloaderPrompt.cs
git commit -m "feat: update agent prompts to guide directory-first exploration"
```

---

### Task 6: Final Verification

**Step 1: Run the full test suite**

Run: `dotnet test Tests -v minimal`
Expected: ALL PASS

**Step 2: Build entire solution**

Run: `dotnet build`
Expected: SUCCESS with no warnings related to our changes

**Step 3: Verify no regressions in MCP server tests**

Run: `dotnet test Tests --filter "McpLibraryServerTests" -v minimal`
Expected: ALL PASS (if these tests exist and run)
