# GlobFiles Tool Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace `ListDirectories` and `ListFiles` MCP tools with a single `GlobFiles` tool that accepts a glob pattern and returns matching file paths.

**Architecture:** Add `GlobFiles` method to `IFileSystemClient`, implement it in `LocalFileSystemClient` using `Microsoft.Extensions.FileSystemGlobbing.Matcher`, create a `GlobFilesTool` domain tool, wrap it in MCP tools for both Library and Text servers, then delete the old tools.

**Tech Stack:** .NET 10, Microsoft.Extensions.FileSystemGlobbing, ModelContextProtocol 0.7.0-preview.1, xUnit + Shouldly

---

### Task 1: Add NuGet Package

**Files:**
- Modify: `Infrastructure/Infrastructure.csproj`

**Step 1: Add the FileSystemGlobbing package**

Add to the `<ItemGroup>` with other `PackageReference` entries in `Infrastructure/Infrastructure.csproj`:

```xml
<PackageReference Include="Microsoft.Extensions.FileSystemGlobbing" Version="10.0.2"/>
```

**Step 2: Restore packages**

Run: `dotnet restore Infrastructure/Infrastructure.csproj`
Expected: Success, no errors

**Step 3: Commit**

```bash
git add Infrastructure/Infrastructure.csproj
git commit -m "chore: add Microsoft.Extensions.FileSystemGlobbing package"
```

---

### Task 2: Add GlobFiles to IFileSystemClient and Implement It (TDD)

**Files:**
- Test: `Tests/Integration/Infrastructure/LocalFileSystemClientTests.cs`
- Modify: `Domain/Contracts/IFileSystemClient.cs`
- Modify: `Infrastructure/Clients/LocalFileSystemClient.cs`

**Step 1: Write the failing tests**

Add these tests to `Tests/Integration/Infrastructure/LocalFileSystemClientTests.cs` (inside the class, after the existing tests):

```csharp
[Fact]
public async Task GlobFiles_WithWildcard_ReturnsMatchingFiles()
{
    // Arrange
    await File.WriteAllTextAsync(Path.Combine(_testDir, "movie.mkv"), "content");
    await File.WriteAllTextAsync(Path.Combine(_testDir, "movie.mp4"), "content");
    await File.WriteAllTextAsync(Path.Combine(_testDir, "readme.txt"), "content");

    // Act
    var files = await _client.GlobFiles(_testDir, "*.mkv");

    // Assert
    files.Length.ShouldBe(1);
    files[0].ShouldEndWith("movie.mkv");
}

[Fact]
public async Task GlobFiles_WithRecursivePattern_ReturnsNestedFiles()
{
    // Arrange
    var subDir = Path.Combine(_testDir, "sub", "deep");
    Directory.CreateDirectory(subDir);
    await File.WriteAllTextAsync(Path.Combine(_testDir, "root.txt"), "content");
    await File.WriteAllTextAsync(Path.Combine(subDir, "nested.txt"), "content");

    // Act
    var files = await _client.GlobFiles(_testDir, "**/*.txt");

    // Assert
    files.Length.ShouldBe(2);
    files.ShouldContain(f => f.EndsWith("root.txt"));
    files.ShouldContain(f => f.EndsWith("nested.txt"));
}

[Fact]
public async Task GlobFiles_WithNoMatches_ReturnsEmptyArray()
{
    // Arrange
    await File.WriteAllTextAsync(Path.Combine(_testDir, "file.txt"), "content");

    // Act
    var files = await _client.GlobFiles(_testDir, "*.pdf");

    // Assert
    files.ShouldBeEmpty();
}

[Fact]
public async Task GlobFiles_WithSubdirectoryPattern_ReturnsOnlyMatchingPath()
{
    // Arrange
    var moviesDir = Path.Combine(_testDir, "movies");
    var booksDir = Path.Combine(_testDir, "books");
    Directory.CreateDirectory(moviesDir);
    Directory.CreateDirectory(booksDir);
    await File.WriteAllTextAsync(Path.Combine(moviesDir, "film.mkv"), "content");
    await File.WriteAllTextAsync(Path.Combine(booksDir, "novel.epub"), "content");

    // Act
    var files = await _client.GlobFiles(_testDir, "movies/**/*");

    // Assert
    files.Length.ShouldBe(1);
    files[0].ShouldEndWith("film.mkv");
}

[Fact]
public async Task GlobFiles_ReturnsAbsolutePaths()
{
    // Arrange
    await File.WriteAllTextAsync(Path.Combine(_testDir, "file.txt"), "content");

    // Act
    var files = await _client.GlobFiles(_testDir, "**/*");

    // Assert
    files.Length.ShouldBe(1);
    files[0].ShouldStartWith(_testDir);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~LocalFileSystemClientTests.GlobFiles" --no-restore`
Expected: Build failure — `GlobFiles` method does not exist on `IFileSystemClient`

**Step 3: Add GlobFiles to the interface**

Add this line to `Domain/Contracts/IFileSystemClient.cs` after the `DescribeDirectory` method:

```csharp
Task<string[]> GlobFiles(string basePath, string pattern, CancellationToken cancellationToken = default);
```

**Step 4: Implement GlobFiles in LocalFileSystemClient**

Add this method to `Infrastructure/Clients/LocalFileSystemClient.cs` after the `DescribeDirectory` method:

```csharp
public Task<string[]> GlobFiles(string basePath, string pattern, CancellationToken cancellationToken = default)
{
    var matcher = new Matcher();
    matcher.AddInclude(pattern);
    var result = matcher.GetResultsInFullPath(basePath);
    return Task.FromResult(result.ToArray());
}
```

Add the using at the top of the file:
```csharp
using Microsoft.Extensions.FileSystemGlobbing;
```

**Step 5: Run tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~LocalFileSystemClientTests.GlobFiles" --no-restore`
Expected: All 5 tests PASS

**Step 6: Commit**

```bash
git add Domain/Contracts/IFileSystemClient.cs Infrastructure/Clients/LocalFileSystemClient.cs Tests/Integration/Infrastructure/LocalFileSystemClientTests.cs
git commit -m "feat: add GlobFiles to IFileSystemClient with Matcher implementation"
```

---

### Task 3: Create GlobFilesTool Domain Tool (TDD)

**Files:**
- Create: `Tests/Unit/Tools/GlobFilesToolTests.cs`
- Create: `Domain/Tools/Files/GlobFilesTool.cs`

**Step 1: Write the failing tests**

Create `Tests/Unit/Tools/GlobFilesToolTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Moq;
using Shouldly;

namespace Tests.Unit.Tools;

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
    public async Task Run_WithValidPattern_ReturnsJsonArray()
    {
        // Arrange
        _mockClient.Setup(c => c.GlobFiles(BasePath, "**/*.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["/library/book.pdf", "/library/sub/doc.pdf"]);

        // Act
        var result = await _tool.TestRun("**/*.pdf", CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        var array = result.AsArray();
        array.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task Run_WithEmptyPattern_ThrowsArgumentException(string? pattern)
    {
        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(
            () => _tool.TestRun(pattern!, CancellationToken.None));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("foo/../../bar")]
    [InlineData("..")]
    public async Task Run_WithDotDotPattern_ThrowsArgumentException(string pattern)
    {
        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(
            () => _tool.TestRun(pattern, CancellationToken.None));
    }

    private class TestableGlobFilesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
        : GlobFilesTool(client, libraryPath)
    {
        public Task<JsonNode> TestRun(string pattern, CancellationToken cancellationToken)
            => Run(pattern, cancellationToken);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~GlobFilesToolTests" --no-restore`
Expected: Build failure — `GlobFilesTool` class does not exist

**Step 3: Create the GlobFilesTool domain tool**

Create `Domain/Tools/Files/GlobFilesTool.cs`:

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
                                         Searches for files matching a glob pattern relative to the library root.
                                         Supports * (single segment), ** (recursive), and ? (single char).
                                         Returns absolute file paths. Examples: **/*.pdf, books/*, src/**/*.cs.
                                         """;

    protected async Task<JsonNode> Run(string pattern, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        if (pattern.Contains(".."))
        {
            throw new ArgumentException("Pattern must not contain '..' segments", nameof(pattern));
        }

        var result = await client.GlobFiles(libraryPath.BaseLibraryPath, pattern, cancellationToken);
        return JsonSerializer.SerializeToNode(result)
               ?? throw new InvalidOperationException("Failed to serialize GlobFiles result");
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~GlobFilesToolTests" --no-restore`
Expected: All 6 tests PASS (1 valid pattern + 3 empty + 2 dotdot, note the `[Theory]` with `[InlineData]` counts)

**Step 5: Commit**

```bash
git add Domain/Tools/Files/GlobFilesTool.cs Tests/Unit/Tools/GlobFilesToolTests.cs
git commit -m "feat: add GlobFilesTool domain tool with input validation"
```

---

### Task 4: Create MCP GlobFiles Tools for Both Servers

**Files:**
- Create: `McpServerLibrary/McpTools/McpGlobFilesTool.cs`
- Create: `McpServerText/McpTools/McpTextGlobFilesTool.cs`

**Step 1: Create the Library server MCP tool**

Create `McpServerLibrary/McpTools/McpGlobFilesTool.cs`:

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
        CancellationToken cancellationToken)
    {
        return ToolResponse.Create(await Run(pattern, cancellationToken));
    }
}
```

**Step 2: Create the Text server MCP tool**

Create `McpServerText/McpTools/McpTextGlobFilesTool.cs`:

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
        CancellationToken cancellationToken)
    {
        return ToolResponse.Create(await Run(pattern, cancellationToken));
    }
}
```

**Step 3: Verify build compiles**

Run: `dotnet build McpServerLibrary/McpServerLibrary.csproj --no-restore && dotnet build McpServerText/McpServerText.csproj --no-restore`
Expected: Both build successfully

**Step 4: Commit**

```bash
git add McpServerLibrary/McpTools/McpGlobFilesTool.cs McpServerText/McpTools/McpTextGlobFilesTool.cs
git commit -m "feat: add MCP GlobFiles tools for Library and Text servers"
```

---

### Task 5: Wire Up New Tools and Remove Old Registrations

**Files:**
- Modify: `McpServerLibrary/Modules/ConfigModule.cs`
- Modify: `McpServerText/Modules/ConfigModule.cs`
- Modify: `Tests/Integration/Fixtures/McpLibraryServerFixture.cs`

**Step 1: Update Library server ConfigModule**

In `McpServerLibrary/Modules/ConfigModule.cs`, replace lines 85-86:
```csharp
            .WithTools<McpListDirectoriesTool>()
            .WithTools<McpListFilesTool>()
```
with:
```csharp
            .WithTools<McpGlobFilesTool>()
```

**Step 2: Update Text server ConfigModule**

In `McpServerText/Modules/ConfigModule.cs`, replace lines 49-50:
```csharp
            .WithTools<McpTextListDirectoriesTool>()
            .WithTools<McpTextListFilesTool>()
```
with:
```csharp
            .WithTools<McpTextGlobFilesTool>()
```

**Step 3: Update test fixture**

In `Tests/Integration/Fixtures/McpLibraryServerFixture.cs`, replace lines 75-76:
```csharp
            .WithTools<McpListDirectoriesTool>()
            .WithTools<McpListFilesTool>()
```
with:
```csharp
            .WithTools<McpGlobFilesTool>()
```

Also update the `using` statement at the top — it already has `using McpServerLibrary.McpTools;` so the new class is in scope. No change needed there.

**Step 4: Verify build compiles**

Run: `dotnet build --no-restore`
Expected: Build succeeds

**Step 5: Commit**

```bash
git add McpServerLibrary/Modules/ConfigModule.cs McpServerText/Modules/ConfigModule.cs Tests/Integration/Fixtures/McpLibraryServerFixture.cs
git commit -m "feat: wire up GlobFiles tools, remove old ListDirectories/ListFiles registrations"
```

---

### Task 6: Update MCP Server Integration Tests

**Files:**
- Modify: `Tests/Integration/McpServerTests/McpLibraryServerTests.cs`

**Step 1: Update the tool list assertion**

In `McpLibraryServerTests.McpServer_IsAccessible_ReturnsAllTools`, replace lines 42-43:
```csharp
        toolNames.ShouldContain("ListDirectories");
        toolNames.ShouldContain("ListFiles");
```
with:
```csharp
        toolNames.ShouldContain("GlobFiles");
```

**Step 2: Replace the ListDirectories and ListFiles test regions**

Replace the entire `#region ListDirectories Tests` and `#region ListFiles Tests` sections (lines 243-342) with:

```csharp
    #region GlobFiles Tests

    [Fact]
    public async Task GlobFilesTool_WithNoFiles_ReturnsEmptyResult()
    {
        // Arrange
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        // Act
        var result = await client.CallToolAsync(
            "GlobFiles",
            new Dictionary<string, object?>
            {
                ["pattern"] = "*.nonexistent"
            },
            cancellationToken: CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.Content.ShouldNotBeNull();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task GlobFilesTool_WithMatchingFiles_ReturnsFileList()
    {
        // Arrange
        fixture.CreateLibraryFile(Path.Combine("GlobTest", "movie1.mkv"));
        fixture.CreateLibraryFile(Path.Combine("GlobTest", "movie2.mkv"));
        fixture.CreateLibraryFile(Path.Combine("GlobTest", "readme.txt"));

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        // Act
        var result = await client.CallToolAsync(
            "GlobFiles",
            new Dictionary<string, object?>
            {
                ["pattern"] = "GlobTest/**/*.mkv"
            },
            cancellationToken: CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        var content = GetTextContent(result);
        content.ShouldContain("movie1.mkv");
        content.ShouldContain("movie2.mkv");
        content.ShouldNotContain("readme.txt");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task GlobFilesTool_WithRecursivePattern_FindsNestedFiles()
    {
        // Arrange
        fixture.CreateLibraryFile(Path.Combine("GlobDeep", "sub1", "file.txt"));
        fixture.CreateLibraryFile(Path.Combine("GlobDeep", "sub2", "nested", "deep.txt"));

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        // Act
        var result = await client.CallToolAsync(
            "GlobFiles",
            new Dictionary<string, object?>
            {
                ["pattern"] = "GlobDeep/**/*.txt"
            },
            cancellationToken: CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        var content = GetTextContent(result);
        content.ShouldContain("file.txt");
        content.ShouldContain("deep.txt");

        await client.DisposeAsync();
    }

    #endregion
```

**Step 3: Run the updated MCP server tests**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~McpLibraryServerTests" --no-restore`
Expected: All tests PASS

**Step 4: Commit**

```bash
git add Tests/Integration/McpServerTests/McpLibraryServerTests.cs
git commit -m "test: update MCP server integration tests for GlobFiles tool"
```

---

### Task 7: Update Agent Integration Tests

**Files:**
- Modify: `Tests/Integration/Agents/McpAgentIntegrationTests.cs`

**Step 1: Replace old agent tests**

Replace the `Agent_WithListDirectoriesTool_CanListLibraryDirectories` test (lines 42-68) and the `Agent_WithListFilesTool_CanListFilesInLibrary` test (lines 102-128) with a single test:

```csharp
    [SkippableFact]
    public async Task Agent_WithGlobFilesTool_CanFindFiles()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        mcpFixture.CreateLibraryFile(Path.Combine("AgentGlobTest", "movie.mkv"));

        var agent = CreateAgent(llmClient);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                "Find all .mkv files in the library using the GlobFiles tool with pattern **/*.mkv",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert - LLM responses are non-deterministic, verify agent processed the request
        responses.ShouldNotBeEmpty();
        var hasContent = responses.Any(r => !string.IsNullOrEmpty(r.Content) || !string.IsNullOrEmpty(r.ToolCalls));
        hasContent.ShouldBeTrue("Agent should have produced content or tool calls");

        await agent.DisposeAsync();
    }
```

**Step 2: Verify build compiles**

Run: `dotnet build Tests/Tests.csproj --no-restore`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add Tests/Integration/Agents/McpAgentIntegrationTests.cs
git commit -m "test: update agent integration tests for GlobFiles tool"
```

---

### Task 8: Delete Old Tool Files and Clean Up Interface

**Files:**
- Delete: `Domain/Tools/Files/ListDirectoriesTool.cs`
- Delete: `Domain/Tools/Files/ListFilesTool.cs`
- Delete: `McpServerLibrary/McpTools/McpListDirectoriesTool.cs`
- Delete: `McpServerLibrary/McpTools/McpListFilesTool.cs`
- Delete: `McpServerText/McpTools/McpTextListDirectoriesTool.cs`
- Delete: `McpServerText/McpTools/McpTextListFilesTool.cs`
- Modify: `Domain/Contracts/IFileSystemClient.cs` — remove `ListDirectoriesIn` and `ListFilesIn`
- Modify: `Infrastructure/Clients/LocalFileSystemClient.cs` — remove `ListDirectoriesIn` and `ListFilesIn` methods
- Modify: `Tests/Integration/Infrastructure/LocalFileSystemClientTests.cs` — remove old tests

**Step 1: Delete old domain tools**

```bash
rm Domain/Tools/Files/ListDirectoriesTool.cs Domain/Tools/Files/ListFilesTool.cs
```

**Step 2: Delete old MCP tool files**

```bash
rm McpServerLibrary/McpTools/McpListDirectoriesTool.cs McpServerLibrary/McpTools/McpListFilesTool.cs
rm McpServerText/McpTools/McpTextListDirectoriesTool.cs McpServerText/McpTools/McpTextListFilesTool.cs
```

**Step 3: Remove ListDirectoriesIn and ListFilesIn from IFileSystemClient**

In `Domain/Contracts/IFileSystemClient.cs`, remove these two lines:
```csharp
    Task<string[]> ListDirectoriesIn(string path, CancellationToken cancellationToken = default);
    Task<string[]> ListFilesIn(string path, CancellationToken cancellationToken = default);
```

**Step 4: Remove ListDirectoriesIn and ListFilesIn from LocalFileSystemClient**

In `Infrastructure/Clients/LocalFileSystemClient.cs`, remove the `ListDirectoriesIn` method and the `ListFilesIn` method.

**Step 5: Remove old tests from LocalFileSystemClientTests**

In `Tests/Integration/Infrastructure/LocalFileSystemClientTests.cs`, remove:
- `ListFilesIn_WithNestedStructure_ReturnsOnlyTopLevelFiles`
- `ListDirectoriesIn_ReturnsOnlyDirectories`
- `ListFilesIn_WithEmptyDirectory_ReturnsEmptyArray`
- `ListDirectoriesIn_WithEmptyDirectory_ReturnsEmptyArray`

**Step 6: Verify everything builds and tests pass**

Run: `dotnet build --no-restore && dotnet test Tests/ --filter "FullyQualifiedName~LocalFileSystemClientTests|FullyQualifiedName~GlobFilesToolTests" --no-restore`
Expected: Build succeeds, all remaining tests pass

**Step 7: Commit**

```bash
git add -A
git commit -m "refactor: remove old ListDirectories/ListFiles tools and interface methods"
```

---

### Task 9: Full Test Suite Verification

**Step 1: Run all unit tests**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~Tests.Unit" --no-restore`
Expected: All PASS

**Step 2: Build the entire solution**

Run: `dotnet build --no-restore`
Expected: Build succeeds with no errors or warnings related to the changes

**Step 3: Final commit (if any fixups needed)**

Only if something needed fixing in the prior steps.
