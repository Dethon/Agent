# Pure Glob Semantics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the `mode` parameter from the virtual-filesystem glob tool and adopt pure glob semantics — the pattern alone decides what matches, a trailing `/` restricts to directories, and directory results are marked with a trailing `/`.

**Architecture:** The glob path spans two layers. Agent-facing: `VfsGlobFilesTool` → `IFileSystemRegistry` → `IFileSystemBackend.GlobAsync` → `McpFileSystemBackend` (over MCP wire). Server-side: each `fs_glob` MCP tool → either `GlobFilesTool`→`IFileSystemClient`/`LocalFileSystemClient` (file mounts) or `HaFileSystem`→`HaTree.Glob` (Home Assistant). Both layers drop `mode`; the matcher merges files and directories and marks directories with a trailing slash.

**Tech Stack:** .NET 10, `Microsoft.Extensions.FileSystemGlobbing`, ModelContextProtocol, xUnit + Shouldly + Moq.

**Spec:** `docs/superpowers/specs/2026-05-23-pure-glob-semantics-design.md`

**Conventions (from CLAUDE.md / rules):**
- NO trailing newline in any `.cs` file (including tests).
- Test command: `dotnet test Tests/Tests.csproj --filter "<expr>"`.
- Some Docker-backed integration tests are unavailable in WSL (~148 pre-existing `DockerUnavailableException` failures, per the test-suite baseline) — they are NOT regressions. Prefer unit tests for verification; for Docker-gated integration tests, confirm they still *compile* and that pre-existing pass/fail counts are unchanged.
- Commit after each task (each task is one compilable unit; the `mode` removal cascades through shared interfaces, so a task bundles an interface change with all its implementors/callers).

---

## File Structure

**Modified — server-side file mounts (Task 1):**
- `Domain/Contracts/IFileSystemClient.cs` — collapse `GlobFiles`+`GlobDirectories` → `Glob`
- `Infrastructure/Clients/LocalFileSystemClient.cs` — unified matcher
- `Domain/Tools/Files/GlobFilesTool.cs` — drop `GlobMode`, single `Run`
- `McpServer{Library,Sandbox,Vault}/McpTools/FsGlobTool.cs` — drop `mode` arg
- Tests: `Tests/Integration/Infrastructure/LocalFileSystemClientTests.cs`, `Tests/Unit/Domain/Tools/GlobFilesToolTests.cs`, `Tests/Integration/McpServerTests/McpLibraryServerTests.cs`

**Modified — Home Assistant (Task 2):**
- `Domain/Tools/HomeAssistant/Vfs/HaTree.cs` — `Glob` drops `directories`, marks dirs
- `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs` — `GlobAsync` drops `GlobMode`
- `McpServerHomeAssistant/McpTools/FsGlobTool.cs` — drop `mode` arg
- `Domain/Prompts/HomeAssistantPrompt.cs` — `glob_files`→`glob`, trailing-slash examples
- Tests: `Tests/Unit/Domain/HomeAssistant/Vfs/HaTreeTests.cs`, `HaFileSystemReadTests.cs`, `HaFileSystemJourneyTests.cs`

**Modified — agent wire layer (Task 3):**
- `Domain/Contracts/IFileSystemBackend.cs` — `GlobAsync` drops `VfsGlobMode`
- `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs` — drop `mode` wire arg
- `Domain/Tools/FileSystem/VfsGlobFilesTool.cs` — rename to `glob`, drop `mode`
- `Domain/Tools/FileSystem/VfsCopyTool.cs` — mode-less glob, skip directory entries
- Tests: `Tests/Unit/Domain/Tools/FileSystem/VfsGlobFilesToolTests.cs`, `VfsTransferDirectoryTests.cs`, `Tests/Integration/Agents/McpAgentFileSystemTests.cs`

**Modified — cleanup (Task 4):**
- `Domain/DTOs/FileSystemEnums.cs` — remove `VfsGlobMode` (keep `VfsTextSearchOutputMode`)
- `Domain/Tools/Files/GlobMode.cs` — delete

---

## Task 1: Server-side file mounts (Local matcher, GlobFilesTool, MCP fs_glob tools)

**Files:**
- Modify: `Domain/Contracts/IFileSystemClient.cs`
- Modify: `Infrastructure/Clients/LocalFileSystemClient.cs:18-47`
- Modify: `Domain/Tools/Files/GlobFilesTool.cs`
- Modify: `McpServerLibrary/McpTools/FsGlobTool.cs`, `McpServerSandbox/McpTools/FsGlobTool.cs`, `McpServerVault/McpTools/FsGlobTool.cs`
- Test: `Tests/Integration/Infrastructure/LocalFileSystemClientTests.cs:245-378`, `Tests/Unit/Domain/Tools/GlobFilesToolTests.cs`, `Tests/Integration/McpServerTests/McpLibraryServerTests.cs:321-390`

- [ ] **Step 1: Rewrite the `LocalFileSystemClient` glob tests for unified `Glob`**

In `Tests/Integration/Infrastructure/LocalFileSystemClientTests.cs`, replace the entire `GlobFiles_*`/`GlobDirectories_*` block (lines 245-378) with these tests against the new `Glob` method:

```csharp
    [Fact]
    public async Task Glob_WithFileWildcard_ReturnsMatchingFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_testDir, "movie.mkv"), "content");
        await File.WriteAllTextAsync(Path.Combine(_testDir, "movie.mp4"), "content");
        await File.WriteAllTextAsync(Path.Combine(_testDir, "readme.txt"), "content");

        var hits = await _client.Glob(_testDir, "*.mkv");

        hits.Length.ShouldBe(1);
        hits[0].ShouldEndWith("movie.mkv");
    }

    [Fact]
    public async Task Glob_NoTrailingSlash_ReturnsFilesAndDirectoriesWithDirsMarked()
    {
        var subDir = Path.Combine(_testDir, "movies");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "todo.md"), "content");

        var hits = await _client.Glob(_testDir, "*");

        hits.ShouldContain(h => h.EndsWith("todo.md") && !h.EndsWith("/"));
        hits.ShouldContain(h => h.EndsWith("movies/"));
    }

    [Fact]
    public async Task Glob_WithTrailingSlash_ReturnsDirectoriesOnly()
    {
        var subDir = Path.Combine(_testDir, "movies");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "todo.md"), "content");

        var hits = await _client.Glob(_testDir, "*/");

        hits.Length.ShouldBe(1);
        hits[0].ShouldEndWith("movies/");
    }

    [Fact]
    public async Task Glob_RecursiveDirsOnly_ReturnsNestedDirectoriesMarked()
    {
        var deep = Path.Combine(_testDir, "movies", "action");
        Directory.CreateDirectory(deep);
        await File.WriteAllTextAsync(Path.Combine(deep, "film.mkv"), "content");

        var hits = await _client.Glob(_testDir, "**/");

        hits.ShouldContain(h => h.EndsWith("movies/"));
        hits.ShouldContain(h => h.EndsWith("action/"));
        hits.ShouldAllBe(h => h.EndsWith("/"));
    }

    [Fact]
    public async Task Glob_ExcludesRootDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, "sub"));

        var hits = await _client.Glob(_testDir, "**/");

        hits.ShouldNotContain(h => h.TrimEnd('/') == _testDir);
    }

    [Fact]
    public async Task Glob_RecursiveFilePattern_ReturnsNestedFiles()
    {
        var subDir = Path.Combine(_testDir, "sub", "deep");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "root.txt"), "content");
        await File.WriteAllTextAsync(Path.Combine(subDir, "nested.txt"), "content");

        var hits = await _client.Glob(_testDir, "**/*.txt");

        hits.Length.ShouldBe(2);
        hits.ShouldContain(h => h.EndsWith("root.txt"));
        hits.ShouldContain(h => h.EndsWith("nested.txt"));
    }

    [Fact]
    public async Task Glob_WithNoMatches_ReturnsEmptyArray()
    {
        await File.WriteAllTextAsync(Path.Combine(_testDir, "file.txt"), "content");

        var hits = await _client.Glob(_testDir, "*.pdf");

        hits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Glob_ReturnsAbsolutePaths()
    {
        await File.WriteAllTextAsync(Path.Combine(_testDir, "file.txt"), "content");

        var hits = await _client.Glob(_testDir, "**/*");

        hits.ShouldAllBe(h => h.StartsWith(_testDir));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~LocalFileSystemClientTests"`
Expected: FAIL — compile error, `'IFileSystemClient' does not contain a definition for 'Glob'`.

- [ ] **Step 3: Update `IFileSystemClient` to expose `Glob`**

In `Domain/Contracts/IFileSystemClient.cs`, replace the two glob lines:

```csharp
    Task<string[]> GlobFiles(string basePath, string pattern, CancellationToken cancellationToken = default);
    Task<string[]> GlobDirectories(string basePath, string pattern, CancellationToken cancellationToken = default);
```

with one:

```csharp
    Task<string[]> Glob(string basePath, string pattern, CancellationToken cancellationToken = default);
```

- [ ] **Step 4: Implement the unified matcher in `LocalFileSystemClient`**

In `Infrastructure/Clients/LocalFileSystemClient.cs`, replace both `GlobFiles` and `GlobDirectories` methods (lines 18-47) with:

```csharp
    public Task<string[]> Glob(string basePath, string pattern, CancellationToken cancellationToken = default)
    {
        var dirsOnly = pattern.EndsWith('/');
        var effectivePattern = dirsOnly ? pattern.TrimEnd('/') : pattern;

        var matcher = new Matcher();
        matcher.AddInclude(effectivePattern);

        var dirRelativePaths = Directory.EnumerateDirectories(basePath, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(basePath, d));
        var matchedDirs = matcher.Match(basePath, dirRelativePaths)
            .Files
            .Select(f => Path.GetFullPath(Path.Combine(basePath, f.Path)))
            .Where(d => d != basePath)
            .Select(d => d + "/");

        if (dirsOnly)
        {
            return Task.FromResult(matchedDirs.Distinct().Order().ToArray());
        }

        var matchedFiles = matcher.GetResultsInFullPath(basePath);
        var result = matchedFiles.Concat(matchedDirs).Distinct().Order().ToArray();
        return Task.FromResult(result);
    }
```

- [ ] **Step 5: Run the `LocalFileSystemClient` tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~LocalFileSystemClientTests"`
Expected: PASS (the codebase still won't fully build until Step 7 updates `GlobFilesTool`; if the Tests project fails to compile due to `GlobFilesTool`, proceed to Steps 6-9 then re-run).

- [ ] **Step 6: Rewrite `GlobFilesToolTests` for the mode-less tool**

In `Tests/Unit/Domain/Tools/GlobFilesToolTests.cs`, replace the whole file body with mode-less tests (note: `GlobMode` and the `TestRun(pattern, GlobMode, …)` overloads are gone):

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools;

public class GlobFilesToolTests
{
    private const string BasePath = "/library";
    private readonly Mock<IFileSystemClient> _mockClient = new();
    private readonly TestableGlobFilesTool _tool;

    public GlobFilesToolTests()
    {
        _tool = new TestableGlobFilesTool(_mockClient.Object, new LibraryPathConfig(BasePath));
    }

    [Fact]
    public async Task Run_WithMatchingEntries_ReturnsEntryList()
    {
        _mockClient.Setup(c => c.Glob(BasePath, "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["/library/book.pdf", "/library/sub/"]);

        var result = await _tool.TestRun("**/*", CancellationToken.None);

        var array = result["entries"]!.AsArray();
        array.Count.ShouldBe(2);
        array.ShouldContain(n => n!.GetValue<string>() == "/library/sub/");
        FsResultContract.TryValidate("fs_glob", result, out var err).ShouldBeTrue(err);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task Run_WithEmptyPattern_ThrowsArgumentException(string? pattern)
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _tool.TestRun(pattern!, CancellationToken.None));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("foo/../../bar")]
    [InlineData("..")]
    public async Task Run_WithDotDotPattern_ThrowsArgumentException(string pattern)
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _tool.TestRun(pattern, CancellationToken.None));
    }

    [Fact]
    public async Task Run_WithAbsolutePathOutsideBasePath_ThrowsArgumentException()
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _tool.TestRun("/other/path/**/*.pdf", CancellationToken.None));
    }

    [Fact]
    public async Task Run_OverCap_ReturnsTruncatedObject()
    {
        var entries = Enumerable.Range(1, 250).Select(i => $"/library/file{i}.pdf").ToArray();
        _mockClient.Setup(c => c.Glob(BasePath, "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        var result = await _tool.TestRun("**/*", CancellationToken.None);

        var obj = result.AsObject();
        obj["truncated"]!.GetValue<bool>().ShouldBeTrue();
        obj["total"]!.GetValue<int>().ShouldBe(250);
        obj["entries"]!.AsArray().Count.ShouldBe(200);
        FsResultContract.TryValidate("fs_glob", result, out var err).ShouldBeTrue(err);
    }

    [Fact]
    public async Task Run_WithBasePath_UsesJoinedRoot()
    {
        var expectedRoot = Path.Combine(BasePath, "docs");
        _mockClient.Setup(c => c.Glob(expectedRoot, "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Path.Combine(expectedRoot, "a.txt")]);

        var result = await _tool.TestRun("**/*", "docs", CancellationToken.None);

        result["entries"]!.AsArray().Count.ShouldBe(1);
        _mockClient.Verify(c => c.Glob(BasePath, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("foo/../bar")]
    public async Task Run_WithBasePathContainingDotDot_ThrowsArgumentException(string basePath)
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _tool.TestRun("**/*", basePath, CancellationToken.None));
    }

    private class TestableGlobFilesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
        : GlobFilesTool(client, libraryPath)
    {
        public Task<JsonNode> TestRun(string pattern, CancellationToken cancellationToken)
            => Run(pattern, cancellationToken);

        public Task<JsonNode> TestRun(string pattern, string basePath, CancellationToken cancellationToken)
            => Run(pattern, cancellationToken, basePath);
    }
}
```

- [ ] **Step 7: Rewrite `GlobFilesTool` for pure semantics**

In `Domain/Tools/Files/GlobFilesTool.cs`, replace the `Description`, the `Run` method, and the `RunDirectories`/`RunFiles` methods. Keep `ResolveMatcherRoot` unchanged. Final file:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;

namespace Domain.Tools.Files;

public class GlobFilesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    protected const string Description = """
                                         Searches for files and directories matching a glob pattern relative to the mount root.
                                         `*` matches one path segment, `**` recurses, `?` matches one character.
                                         A trailing slash matches directories only (e.g. `*/`, `src/**/`); otherwise both files
                                         and directories match, with directory results returned with a trailing slash so you can
                                         tell them apart. Results are capped at 200; the response is `{entries, truncated, total}`.
                                         An empty result means nothing matched—refine the pattern.
                                         """;

    private const int FileResultCap = 200;

    protected async Task<JsonNode> Run(string pattern, CancellationToken cancellationToken, string? basePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        if (pattern.Contains(".."))
        {
            throw new ArgumentException("Pattern must not contain '..' segments", nameof(pattern));
        }

        if (Path.IsPathRooted(pattern))
        {
            if (!pattern.StartsWith(libraryPath.BaseLibraryPath, StringComparison.Ordinal))
            {
                throw new ArgumentException("Absolute pattern must be under the library root", nameof(pattern));
            }

            pattern = Path.GetRelativePath(libraryPath.BaseLibraryPath, pattern);
        }

        var matcherRoot = ResolveMatcherRoot(basePath);
        var result = await client.Glob(matcherRoot, pattern, cancellationToken);
        var capped = result.Length > FileResultCap;

        return FsResultContract.ToNode(new FsGlobResult
        {
            Entries = capped ? result.Take(FileResultCap).ToArray() : result,
            Truncated = capped,
            Total = result.Length
        });
    }

    private string ResolveMatcherRoot(string? basePath)
    {
        if (string.IsNullOrEmpty(basePath))
        {
            return libraryPath.BaseLibraryPath;
        }

        if (basePath.Contains(".."))
        {
            throw new ArgumentException("basePath must not contain '..' segments", nameof(basePath));
        }

        var combined = Path.Combine(libraryPath.BaseLibraryPath, basePath.TrimStart('/'));
        var canonRoot = Path.GetFullPath(combined);
        var canonBase = Path.GetFullPath(libraryPath.BaseLibraryPath);

        if (!canonRoot.StartsWith(canonBase, StringComparison.Ordinal))
        {
            throw new ArgumentException("basePath must resolve under the library root", nameof(basePath));
        }

        return canonRoot;
    }
}
```

Note: the file must end without a trailing newline.

- [ ] **Step 8: Drop the `mode` arg from the three file-mount `fs_glob` tools**

In each of `McpServerLibrary/McpTools/FsGlobTool.cs`, `McpServerSandbox/McpTools/FsGlobTool.cs`, `McpServerVault/McpTools/FsGlobTool.cs`, replace the `McpRun` method body with (identical in all three):

```csharp
    [McpServerTool(Name = "fs_glob")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string pattern,
        string basePath = "",
        CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(await Run(pattern, cancellationToken, basePath));
    }
```

Then remove the now-unused `using Domain.Tools.Files;` only if `GlobMode` was the sole symbol used from it — keep the `using` since `GlobFilesTool` (the base class) lives in `Domain.Tools.Files`. Leave all other usings.

- [ ] **Step 9: Update the Library MCP integration glob assertions**

In `Tests/Integration/McpServerTests/McpLibraryServerTests.cs`, the `GlobFilesTool_*` tests (lines 321-390) pass `["mode"] = "files"` style args. Update each `fs_glob` call's argument dictionary to drop any `["mode"]` entry (keep `["pattern"]`/`["basePath"]`). For the recursive test, since results now include directories, change assertions that count exact files to use `ShouldContain` on the expected file paths rather than exact `.Count` equality. (Open the file, locate each `["mode"]` key inside the `fs_glob` argument dictionaries, delete those lines; adjust any `entries.Count.ShouldBe(N)` that would now also count directories to `entries.ShouldContain(...)`.)

- [ ] **Step 10: Run the affected unit + local-FS tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~LocalFileSystemClientTests|FullyQualifiedName~GlobFilesToolTests"`
Expected: PASS. (The `McpLibraryServerTests` are Docker-gated; confirm they compile by building: `dotnet build Tests/Tests.csproj` succeeds.)

- [ ] **Step 11: Commit**

```bash
git add Domain/Contracts/IFileSystemClient.cs Infrastructure/Clients/LocalFileSystemClient.cs Domain/Tools/Files/GlobFilesTool.cs McpServerLibrary/McpTools/FsGlobTool.cs McpServerSandbox/McpTools/FsGlobTool.cs McpServerVault/McpTools/FsGlobTool.cs Tests/Integration/Infrastructure/LocalFileSystemClientTests.cs Tests/Unit/Domain/Tools/GlobFilesToolTests.cs Tests/Integration/McpServerTests/McpLibraryServerTests.cs
git commit -m "feat(vfs): pure glob semantics for file-backed mounts (drop mode)"
```

---

## Task 2: Home Assistant glob

**Files:**
- Modify: `Domain/Tools/HomeAssistant/Vfs/HaTree.cs:58-64`
- Modify: `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs:17-30`
- Modify: `McpServerHomeAssistant/McpTools/FsGlobTool.cs`
- Modify: `Domain/Prompts/HomeAssistantPrompt.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaTreeTests.cs:38-50`, `HaFileSystemReadTests.cs`, `HaFileSystemJourneyTests.cs`

- [ ] **Step 1: Rewrite the `HaTree.Glob` tests**

In `Tests/Unit/Domain/HomeAssistant/Vfs/HaTreeTests.cs`, replace the two glob tests (around lines 38-50) with mode-less versions (the `directories:` arg is gone; directories now carry a trailing slash):

```csharp
    [Fact]
    public void Glob_TrailingSlash_ReturnsDirectoriesMarkedWithSlash()
    {
        var hits = HaTree.Glob(Cat(), "entities/light", "*/");

        hits.ShouldAllBe(h => h.EndsWith("/"));
        hits.ShouldNotBeEmpty();
    }

    [Fact]
    public void Glob_NoTrailingSlash_MatchesFiles()
    {
        var hits = HaTree.Glob(Cat(), "entities", "**/*.sh");

        hits.ShouldNotBeEmpty();
        hits.ShouldAllBe(h => h.EndsWith(".sh"));
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaTreeTests"`
Expected: FAIL — `HaTree.Glob` no longer accepts a `directories` argument (compile error).

- [ ] **Step 3: Rewrite `HaTree.Glob`**

In `Domain/Tools/HomeAssistant/Vfs/HaTree.cs`, replace the `Glob` method (lines 58-64) with:

```csharp
    public static IReadOnlyList<string> Glob(HaCatalog catalog, string basePath, string pattern)
    {
        var dirsOnly = pattern.EndsWith('/');
        var effectivePattern = dirsOnly ? pattern.TrimEnd('/') : pattern;
        var prefix = string.IsNullOrEmpty(basePath) ? string.Empty : basePath.Trim('/') + "/";
        var regex = GlobToRegex(prefix + effectivePattern);

        var dirs = Directories(catalog).Where(p => regex.IsMatch(p)).Select(p => p + "/");
        if (dirsOnly)
        {
            return dirs.OrderBy(p => p, StringComparer.Ordinal).ToList();
        }

        var files = Files(catalog).Where(p => regex.IsMatch(p));
        return dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }
```

- [ ] **Step 4: Update `HaFileSystem.GlobAsync`**

In `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs`, replace the `GlobAsync` method (lines 19-30) with (drop the `GlobMode` parameter; keep the uncapped comment):

```csharp
    // Glob is uncapped: the result set is bounded by the home's entity count.
    public async Task<JsonNode> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        var hits = HaTree.Glob(catalog, basePath, pattern);
        var entries = hits.ToList();
        return FsResultContract.ToNode(new FsGlobResult
        {
            Entries = entries,
            Truncated = false,
            Total = entries.Count
        });
    }
```

If `using Domain.Tools.Files;` (which provided `GlobMode`) is now unused in this file, remove that single using line. Verify no other symbol from `Domain.Tools.Files` is referenced before removing.

- [ ] **Step 5: Update the HA `fs_glob` MCP tool**

In `McpServerHomeAssistant/McpTools/FsGlobTool.cs`, replace the class body's `McpRun` with:

```csharp
    [McpServerTool(Name = "fs_glob")]
    [Description("Lists Home Assistant entities, areas, and action files matching a glob pattern. "
        + "`*` matches one path segment, `**` recurses. A trailing slash lists directories only "
        + "(domains, entities, areas — e.g. `*/`); otherwise files (`state.json`, `*.sh`) and "
        + "directories both match, with directories returned with a trailing slash.")]
    public async Task<CallToolResult> McpRun(
        string pattern,
        string basePath = "",
        CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(await fs.GlobAsync(basePath, pattern, cancellationToken));
    }
```

Remove `using Domain.Tools.Files;` from this file (it was only for `GlobMode`).

- [ ] **Step 6: Update `HaFileSystem` glob callers in HA tests**

In `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs` and `HaFileSystemJourneyTests.cs`, every `fs.GlobAsync(path, "*", GlobMode.Directories, CancellationToken.None)` call must drop the `GlobMode.Directories` argument. Since these calls intend to list directories, append a trailing slash to the pattern and remove the mode arg:
- `HaFileSystemReadTests.cs:31` → `await fs.GlobAsync("entities/light", "*/", CancellationToken.None);`
- `HaFileSystemReadTests.cs:117` → `await fs.GlobAsync("areas/salon", "*/", CancellationToken.None);`
- `HaFileSystemJourneyTests.cs:27` → `await fs.GlobAsync("entities", "*/", CancellationToken.None);`

Then update any assertion in those tests that compares entry strings to bare directory names so it expects the trailing-slash form (e.g. an expected `"entities/light/kitchen_(kitchen)"` becomes `"entities/light/kitchen_(kitchen)/"`). Open each test, locate the post-glob assertions, and add the trailing `/` to expected directory entries. Remove the now-unused `using` for `GlobMode` if present.

- [ ] **Step 7: Update the Home Assistant prompt**

In `Domain/Prompts/HomeAssistantPrompt.cs`, replace each `glob_files` with `glob` (4 occurrences at lines ~29, ~35, ~37, ~71) and update the directory-listing examples to use a trailing slash:
- Line ~37: `actions, `glob` `<entity-dir>/*.sh`.` (file pattern, no trailing slash — unchanged besides rename)
- Line ~71: change `(`glob_files /ha/areas/*` lists them)` to `(`glob /ha/areas/*/` lists them)`

Make the edits as exact-string replacements so surrounding prose is preserved.

- [ ] **Step 8: Run HA unit tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaTreeTests|FullyQualifiedName~HaFileSystemReadTests|FullyQualifiedName~HaFileSystemJourneyTests"`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaTree.cs Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs McpServerHomeAssistant/McpTools/FsGlobTool.cs Domain/Prompts/HomeAssistantPrompt.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaTreeTests.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemReadTests.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemJourneyTests.cs
git commit -m "feat(ha-vfs): pure glob semantics (drop mode, trailing-slash dirs)"
```

---

## Task 3: Agent wire layer (backend contract, VfsGlobFilesTool rename, VfsCopyTool)

**Files:**
- Modify: `Domain/Contracts/IFileSystemBackend.cs:14`
- Modify: `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs:52-60`
- Modify: `Domain/Tools/FileSystem/VfsGlobFilesTool.cs`
- Modify: `Domain/Tools/FileSystem/VfsCopyTool.cs:111` (and the entry loop)
- Test: `Tests/Unit/Domain/Tools/FileSystem/VfsGlobFilesToolTests.cs`, `VfsTransferDirectoryTests.cs`, `Tests/Integration/Agents/McpAgentFileSystemTests.cs`

- [ ] **Step 1: Rewrite `VfsGlobFilesToolTests` for the mode-less, renamed tool**

Replace the body of `Tests/Unit/Domain/Tools/FileSystem/VfsGlobFilesToolTests.cs` with:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class GlobFilesToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsGlobFilesTool _tool;

    public GlobFilesToolTests()
    {
        _tool = new VfsGlobFilesTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesBasePathAndCallsBackend()
    {
        var expected = new JsonObject { ["entries"] = new JsonArray("a.md", "sub/"), ["truncated"] = false, ["total"] = 2 };
        _registry.Setup(r => r.Resolve("/library"))
            .Returns(new FileSystemResolution(_backend.Object, ""));
        _backend.Setup(b => b.GlobAsync("", "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/library", "**/*", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_WithSubdirectory_ResolvesRelativePath()
    {
        var expected = new JsonObject { ["entries"] = new JsonArray("docs/"), ["truncated"] = false, ["total"] = 1 };
        _registry.Setup(r => r.Resolve("/vault/docs"))
            .Returns(new FileSystemResolution(_backend.Object, "docs"));
        _backend.Setup(b => b.GlobAsync("docs", "*/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/vault/docs", "*/", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task Name_IsGlob()
    {
        VfsGlobFilesTool.Name.ShouldBe("glob");
        VfsGlobFilesTool.Key.ShouldBe("glob");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsGlobFilesToolTests"`
Expected: FAIL — `GlobAsync` still requires a `VfsGlobMode` argument; `Name` is `"glob_files"`.

- [ ] **Step 3: Update `IFileSystemBackend`**

In `Domain/Contracts/IFileSystemBackend.cs`, change line 14 from:

```csharp
    Task<JsonNode> GlobAsync(string basePath, string pattern, VfsGlobMode mode, CancellationToken ct);
```

to:

```csharp
    Task<JsonNode> GlobAsync(string basePath, string pattern, CancellationToken ct);
```

- [ ] **Step 4: Update `McpFileSystemBackend`**

In `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`, replace `GlobAsync` (lines 52-60) with:

```csharp
    public async Task<JsonNode> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        return await CallToolAsync("fs_glob", new Dictionary<string, object?>
        {
            ["basePath"] = basePath,
            ["pattern"] = pattern
        }, ct);
    }
```

- [ ] **Step 5: Rewrite `VfsGlobFilesTool`**

Replace the whole of `Domain/Tools/FileSystem/VfsGlobFilesTool.cs` with:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class VfsGlobFilesTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "glob";
    public const string Name = "glob";

    public const string ToolDescription = """
        Searches a filesystem for files and directories matching a glob pattern. The pattern
        alone decides what matches — there is no mode. `*` matches one path segment, `**`
        recurses, `?` matches one character. A trailing slash restricts the match to
        directories (e.g. `*/`, `src/**/`); without it, both files and directories match.
        Directory results are returned with a trailing slash so you can tell them apart; files
        are not. Results are lexically sorted and capped at 200 on file mounts; the response is
        `{entries, truncated, total}`. An empty result means nothing matched the pattern.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual base path to search from (e.g., /library or /library/docs)")]
        string basePath,
        [Description("Glob pattern. `*` = one segment, `**` = recursive, `?` = one char. "
            + "A trailing slash (e.g. `*/`, `src/**/`) matches directories only; otherwise files "
            + "and directories both match, with directory results marked by a trailing slash.")]
        string pattern,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(basePath);
        return await resolution.Backend.GlobAsync(resolution.RelativePath, pattern, cancellationToken);
    }
}
```

Note: `using Domain.DTOs;` is dropped because `VfsGlobMode` is no longer referenced. File ends without a trailing newline.

- [ ] **Step 6: Update the `VfsCopyTool` directory-transfer glob**

In `Domain/Tools/FileSystem/VfsCopyTool.cs`, change line 111 from:

```csharp
        var glob = await src.Backend.GlobAsync(src.RelativePath, "**/*", VfsGlobMode.Files, ct);
```

to:

```csharp
        var glob = await src.Backend.GlobAsync(src.RelativePath, "**/*", ct);
```

Pure glob now returns directories too (marked with a trailing `/`); the transfer copies file contents only, so skip directory entries. In the `foreach (var entry in entries)` loop, immediately after computing `srcRel` (the block at lines 122-124), insert:

```csharp
            if (srcRel.EndsWith('/'))
            {
                continue;
            }
```

If `using Domain.DTOs;` in `VfsCopyTool.cs` is now unused (it was for `VfsGlobMode`), remove that single using line — but first verify no other `Domain.DTOs` symbol is referenced in the file.

- [ ] **Step 7: Update `VfsTransferDirectoryTests`**

In `Tests/Unit/Domain/Tools/FileSystem/VfsTransferDirectoryTests.cs`, every `src.Setup(b => b.GlobAsync("src", "**/*", VfsGlobMode.Files, It.IsAny<CancellationToken>()))` (lines 17, 62, 100, 148, 184) must drop the `VfsGlobMode.Files` argument:

```csharp
        src.Setup(b => b.GlobAsync("src", "**/*", It.IsAny<CancellationToken>()))
```

After that change `VfsGlobMode` is no longer referenced, so delete the now-unused `using Domain.DTOs;` (line 4).

Add one new test proving directory entries (trailing-slash) are skipped during transfer. It mirrors the existing tests' invocation exactly — `VfsCopyTool.TransferDirectoryAsync(...)` (static) and `AsyncEnumerableTestHelpers.ToAsyncEnumerable(...)`. Append it to the class:

```csharp
    [Fact]
    public async Task TransferDirectoryAsync_SkipsDirectoryEntries()
    {
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.GlobAsync("src", "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject
            {
                ["entries"] = new JsonArray("src/sub/", "src/sub/a.md"),
                ["truncated"] = false,
                ["total"] = 2
            });
        src.Setup(b => b.ReadChunksAsync("src/sub/a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("A")));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var srcRes = new FileSystemResolution(src.Object, "src");
        var dstRes = new FileSystemResolution(dst.Object, "dst");

        var result = await VfsCopyTool.TransferDirectoryAsync(
            srcRes, dstRes, "/vault/src", "/sandbox/dst",
            overwrite: false, createDirectories: true, deleteSource: false, CancellationToken.None);

        result["summary"]!["transferred"]!.GetValue<int>().ShouldBe(1);
        result["summary"]!["failed"]!.GetValue<int>().ShouldBe(0);
        dst.Verify(b => b.WriteChunksAsync("dst/sub/a.md",
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            false, true, It.IsAny<CancellationToken>()), Times.Once);
        dst.Verify(b => b.WriteChunksAsync(It.Is<string>(p => p.EndsWith("/")),
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }
```

- [ ] **Step 8: Update the agent integration test tool name**

In `Tests/Integration/Agents/McpAgentFileSystemTests.cs`, replace `domain__filesystem__glob_files` with `domain__filesystem__glob` (lines ~147 and ~302). The `enabledTools` set already uses `"glob"` (line 296) — no change there.

- [ ] **Step 9: Run the affected unit tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsGlobFilesToolTests|FullyQualifiedName~VfsTransferDirectoryTests"`
Expected: PASS. Then `dotnet build Tests/Tests.csproj` to confirm the Docker-gated `McpAgentFileSystemTests` compiles.

- [ ] **Step 10: Commit**

```bash
git add Domain/Contracts/IFileSystemBackend.cs Infrastructure/Agents/Mcp/McpFileSystemBackend.cs Domain/Tools/FileSystem/VfsGlobFilesTool.cs Domain/Tools/FileSystem/VfsCopyTool.cs Tests/Unit/Domain/Tools/FileSystem/VfsGlobFilesToolTests.cs Tests/Unit/Domain/Tools/FileSystem/VfsTransferDirectoryTests.cs Tests/Integration/Agents/McpAgentFileSystemTests.cs
git commit -m "feat(vfs): pure glob semantics at agent layer; rename tool to glob"
```

---

## Task 4: Remove the now-dead glob enums and verify the whole change

**Files:**
- Modify: `Domain/DTOs/FileSystemEnums.cs`
- Delete: `Domain/Tools/Files/GlobMode.cs`

- [ ] **Step 1: Confirm both enums are unreferenced**

Run: `grep -rn "VfsGlobMode\|GlobMode" --include=*.cs . | grep -v "VfsTextSearchOutputMode"`
Expected: no matches (other than possibly the enum definitions themselves). If any non-definition reference remains, fix it before deleting (it belongs to one of Tasks 1-3).

- [ ] **Step 2: Remove `VfsGlobMode` from `FileSystemEnums.cs`**

In `Domain/DTOs/FileSystemEnums.cs`, delete the `VfsGlobMode` enum, keeping `VfsTextSearchOutputMode`. Final file:

```csharp
namespace Domain.DTOs;

public enum VfsTextSearchOutputMode
{
    Content,
    FilesOnly
}
```

- [ ] **Step 3: Delete `GlobMode.cs`**

Run: `git rm Domain/Tools/Files/GlobMode.cs`

- [ ] **Step 4: Build the whole solution**

Run: `dotnet build`
Expected: build succeeds with no errors.

- [ ] **Step 5: Run the full non-E2E unit/integration suite**

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E"`
Expected: the only failures are the ~148 pre-existing `DockerUnavailableException` integration failures (per the test-suite baseline). No NEW failures, and all glob-related unit tests pass. Compare the failure count/identities against the baseline to confirm no regressions.

- [ ] **Step 6: Commit**

```bash
git add Domain/DTOs/FileSystemEnums.cs Domain/Tools/Files/GlobMode.cs
git commit -m "chore(vfs): remove dead glob mode enums"
```

---

## Self-Review

**Spec coverage:**
- Drop `mode`, pattern decides matches → Tasks 1-3 (all `GlobAsync`/`Glob`/`Run` signatures).
- Trailing `/` = directories only → LocalFileSystemClient (T1 S4), HaTree (T2 S3), GlobFilesTool description (T1 S7), VfsGlobFilesTool description (T3 S5).
- Directory entries marked with trailing `/` → LocalFileSystemClient (T1 S4), HaTree (T2 S3); consumed by VfsCopyTool skip (T3 S6).
- Result shape `{entries, truncated, total}` unchanged, cap 200 on file mounts, HA uncapped → GlobFilesTool (T1 S7), HaFileSystem (T2 S4).
- Rename `glob_files`→`glob` → VfsGlobFilesTool (T3 S5), prompts (T2 S7), integration tests (T3 S8).
- Wire arg dropped across 4 servers + backend client → T1 S8, T2 S5, T3 S4.
- Unified matcher replacing the file-only `Matcher` two-pass hack → T1 S4 (parity preserved by reusing `GetResultsInFullPath` for files).
- Enum cleanup → T4.
- TDD RED-first per layer → every task leads with a failing test.

**Placeholder scan:** None. T3 S7 uses the real invocation (`VfsCopyTool.TransferDirectoryAsync(...)`, `AsyncEnumerableTestHelpers.ToAsyncEnumerable(...)`) confirmed from the existing test file. The only judgement call left to the implementer is in T1 S9 (adjusting `McpLibraryServerTests` count-assertions that now also see directories) — that test file is Docker-gated and its exact assertions must be read at edit time; the required transformation (drop `["mode"]`, switch exact `.Count` to `ShouldContain`) is specified.

**Type consistency:** `Glob(basePath, pattern, ct)` used identically in `IFileSystemClient`, `LocalFileSystemClient`, and `GlobFilesTool`. `GlobAsync(basePath, pattern, ct)` used identically in `IFileSystemBackend`, `McpFileSystemBackend`, `VfsGlobFilesTool`, `VfsCopyTool`. `HaTree.Glob(catalog, basePath, pattern)` and `HaFileSystem.GlobAsync(basePath, pattern, ct)` consistent across Task 2. Tool name constant `"glob"` consistent (T3 S5) with prompt and integration-test references.
