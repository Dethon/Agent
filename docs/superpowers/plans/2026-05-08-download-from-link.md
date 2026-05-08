# download_file from a link — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the `download_file` MCP tool so Captain Agent can also start a torrent download by passing a magnet/`.torrent` link plus a title (in addition to the existing search-result id), enabling a web-search fallback path when Jackett returns nothing.

**Architecture:** `Domain.Tools.Downloads.FileDownloadTool` exposes a polymorphic protected `Run` overload for the link path. It hashes `link.GetHashCode()` to derive an int id (matching the existing `JackettSearchClient.ParseTorznabItem` convention), seeds `ISearchResultsManager` with a synthetic `SearchResult`, then funnels through a private `StartDownload` helper shared with the search-id path. The MCP wrapper (`McpServerLibrary.McpTools.McpFileDownloadTool`) gains optional `link`/`title` parameters and a static `ValidateInputs` that enforces XOR + prefix rules at the tool boundary. Captain Agent's prompt is updated to introduce the web-search fallback. No changes to `IDownloadClient`, `ITrackedDownloadsManager`, `ISearchResultsManager`, the cleanup/status/resubscribe tools, or any DTOs.

**Tech Stack:** .NET 10 LTS · ModelContextProtocol SDK · xUnit · Moq · Shouldly · qBittorrent (existing integration) · FluentResults (existing).

**Reference:** Spec at `docs/superpowers/specs/2026-05-08-download-from-link-design.md`. House rules at `.claude/rules/{dotnet-style,domain-layer,infrastructure-layer,mcp-tools,testing}.md` and `CLAUDE.md`.

**Branch:** `jack-download-link-support` (already current; spec was committed there as `2dbfedd8`).

---

## File Map

**Modify:**
- `Domain/Tools/Downloads/FileDownloadTool.cs` — add link-path `Run` overload, extract `StartDownload` helper.
- `McpServerLibrary/McpTools/McpFileDownloadTool.cs` — add optional `link`/`title` params, dispatch, static `ValidateInputs`.
- `Domain/Prompts/DownloaderPrompt.cs` — add Phase 1 fallback bullet about web search.
- `Tests/Integration/McpServerTests/McpLibraryServerTests.cs` — add link-path E2E case.

**Create:**
- `Tests/Unit/Domain/FileDownloadToolTests.cs` — new file, both search-id and link-path cases.
- `Tests/Unit/McpServerLibrary/McpFileDownloadToolTests.cs` — new file, MCP-boundary validation cases.

**Untouched but relevant context (do NOT modify):**
- `Domain/Contracts/IDownloadClient.cs`, `ISearchResultsManager.cs`, `ITrackedDownloadsManager.cs` — interfaces stay as-is.
- `Domain/DTOs/SearchResult.cs` — DTO stays as-is.
- `Infrastructure/Clients/Torrent/JackettSearchClient.cs` — reference for `Id = link.GetHashCode()` convention.
- `Infrastructure/Utils/ToolResponse.cs` — `ToolResponse.Create(JsonNode)` will surface our validation envelopes via `IsError`.

---

## Task 1: Domain — link-path overload with TDD

**Files:**
- Create: `Tests/Unit/Domain/FileDownloadToolTests.cs`
- Modify: `Domain/Tools/Downloads/FileDownloadTool.cs`

### Step 1: Write the failing tests

Create `Tests/Unit/Domain/FileDownloadToolTests.cs` with both characterization tests for the existing search-id path and new tests for the link path:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Config;
using Domain.Tools.Downloads;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain;

public class FileDownloadToolTests
{
    private readonly Mock<IDownloadClient> _downloadClientMock = new();
    private readonly Mock<ISearchResultsManager> _searchResultsManagerMock = new();
    private readonly Mock<ITrackedDownloadsManager> _trackedDownloadsManagerMock = new();
    private readonly DownloadPathConfig _pathConfig = new("/downloads");

    private TestableFileDownloadTool CreateTool()
    {
        return new TestableFileDownloadTool(
            _downloadClientMock.Object,
            _searchResultsManagerMock.Object,
            _trackedDownloadsManagerMock.Object,
            _pathConfig);
    }

    [Fact]
    public async Task Run_SearchId_HappyPath_StartsDownloadAndTracks()
    {
        // Arrange
        const int searchResultId = 42;
        const string link = "magnet:?xt=urn:btih:abc";
        var searchResult = new SearchResult
        {
            Id = searchResultId,
            Title = "The Lost City of Z 1080p",
            Link = link
        };
        _downloadClientMock
            .Setup(m => m.GetDownloadItem(searchResultId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DownloadItem?)null);
        _searchResultsManagerMock
            .Setup(m => m.Get("session1", searchResultId))
            .Returns(searchResult);

        var tool = CreateTool();

        // Act
        var result = await tool.TestRun("session1", searchResultId, CancellationToken.None);

        // Assert
        result["status"]!.GetValue<string>().ShouldBe("success");
        _downloadClientMock.Verify(
            m => m.Download(link, "/downloads/42", searchResultId, It.IsAny<CancellationToken>()),
            Times.Once);
        _trackedDownloadsManagerMock.Verify(m => m.Add("session1", searchResultId), Times.Once);
    }

    [Fact]
    public async Task Run_SearchId_AlreadyExists_ReturnsAlreadyExistsEnvelope()
    {
        // Arrange
        const int searchResultId = 42;
        _downloadClientMock
            .Setup(m => m.GetDownloadItem(searchResultId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadItem
            {
                Id = searchResultId,
                Title = "x",
                Link = "x",
                SavePath = "x",
                State = DownloadState.InProgress,
                Progress = 0,
                DownSpeed = 0,
                UpSpeed = 0,
                Eta = 0
            });

        var tool = CreateTool();

        // Act
        var result = await tool.TestRun("session1", searchResultId, CancellationToken.None);

        // Assert
        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("already_exists");
        _downloadClientMock.Verify(
            m => m.Download(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_SearchId_NotInCache_ReturnsNotFoundEnvelope()
    {
        // Arrange
        const int searchResultId = 42;
        _downloadClientMock
            .Setup(m => m.GetDownloadItem(searchResultId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DownloadItem?)null);
        _searchResultsManagerMock
            .Setup(m => m.Get("session1", searchResultId))
            .Returns((SearchResult?)null);

        var tool = CreateTool();

        // Act
        var result = await tool.TestRun("session1", searchResultId, CancellationToken.None);

        // Assert
        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("not_found");
        _downloadClientMock.Verify(
            m => m.Download(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_Link_HappyPath_SeedsCacheAndStartsDownload()
    {
        // Arrange
        const string link = "magnet:?xt=urn:btih:web-found";
        const string title = "Web Found Title 1080p";
        var expectedId = link.GetHashCode();

        _downloadClientMock
            .Setup(m => m.GetDownloadItem(expectedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DownloadItem?)null);

        var tool = CreateTool();

        // Act
        var result = await tool.TestRun("session1", link, title, CancellationToken.None);

        // Assert
        result["status"]!.GetValue<string>().ShouldBe("success");

        _searchResultsManagerMock.Verify(
            m => m.Add(
                "session1",
                It.Is<SearchResult[]>(arr =>
                    arr.Length == 1 &&
                    arr[0].Id == expectedId &&
                    arr[0].Title == title &&
                    arr[0].Link == link)),
            Times.Once);

        _downloadClientMock.Verify(
            m => m.Download(link, $"/downloads/{expectedId}", expectedId, It.IsAny<CancellationToken>()),
            Times.Once);
        _trackedDownloadsManagerMock.Verify(m => m.Add("session1", expectedId), Times.Once);
    }

    [Fact]
    public async Task Run_Link_AlreadyExists_ReturnsAlreadyExistsEnvelopeWithoutSeedingOrAdding()
    {
        // Arrange
        const string link = "magnet:?xt=urn:btih:dup";
        const string title = "Duplicate";
        var id = link.GetHashCode();

        _downloadClientMock
            .Setup(m => m.GetDownloadItem(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadItem
            {
                Id = id,
                Title = "x",
                Link = link,
                SavePath = "x",
                State = DownloadState.InProgress,
                Progress = 0,
                DownSpeed = 0,
                UpSpeed = 0,
                Eta = 0
            });

        var tool = CreateTool();

        // Act
        var result = await tool.TestRun("session1", link, title, CancellationToken.None);

        // Assert
        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("already_exists");

        _searchResultsManagerMock.Verify(
            m => m.Add(It.IsAny<string>(), It.IsAny<SearchResult[]>()),
            Times.Never);
        _downloadClientMock.Verify(
            m => m.Download(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _trackedDownloadsManagerMock.Verify(
            m => m.Add(It.IsAny<string>(), It.IsAny<int>()),
            Times.Never);
    }

    private class TestableFileDownloadTool(
        IDownloadClient client,
        ISearchResultsManager searchResultsManager,
        ITrackedDownloadsManager trackedDownloadsManager,
        DownloadPathConfig pathConfig)
        : FileDownloadTool(client, searchResultsManager, trackedDownloadsManager, pathConfig)
    {
        public Task<JsonNode> TestRun(string sessionId, int searchResultId, CancellationToken ct)
            => Run(sessionId, searchResultId, ct);

        public Task<JsonNode> TestRun(string sessionId, string link, string title, CancellationToken ct)
            => Run(sessionId, link, title, ct);
    }
}
```

### Step 2: Run the tests and verify they fail

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FileDownloadToolTests" -v normal`

Expected: The two link-path tests (`Run_Link_HappyPath_...` and `Run_Link_AlreadyExists_...`) fail with a compile error referencing the missing `Run(string, string, string, CancellationToken)` overload on `FileDownloadTool`. The three search-id tests would also fail to compile because of the same compilation unit error — that's expected; both groups will go green together once the overload exists.

### Step 3: Implement the link-path overload + extract `StartDownload`

Replace the contents of `Domain/Tools/Downloads/FileDownloadTool.cs` with:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Config;

namespace Domain.Tools.Downloads;

public class FileDownloadTool(
    IDownloadClient client,
    ISearchResultsManager searchResultsManager,
    ITrackedDownloadsManager trackedDownloadsManager,
    DownloadPathConfig pathConfig)
{
    protected const string Name = "download_file";

    protected const string Description = """
                                         Download a file from the internet.

                                         Provide ONE of:
                                           - searchResultId: an id from a prior file_search call.
                                           - link + title: a magnet URI or .torrent URL the agent obtained via web tools
                                             (web_search / web_browse / web_snapshot), plus a descriptive title (e.g. the
                                             release name with quality and group, taken from the page where the link was found).

                                         Do not provide both. The link path is intended as a fallback when file_search returns
                                         no usable results.
                                         """;

    protected async Task<JsonNode> Run(string sessionId, int searchResultId, CancellationToken ct)
    {
        var existing = await client.GetDownloadItem(searchResultId, ct);
        if (existing is not null)
        {
            return ToolError.Create(
                ToolError.Codes.AlreadyExists,
                "Download with this id already exists, try another id",
                retryable: false);
        }

        var itemToDownload = searchResultsManager.Get(sessionId, searchResultId);
        if (itemToDownload == null)
        {
            return ToolError.Create(
                ToolError.Codes.NotFound,
                $"No search result found for id {searchResultId}. " +
                "Make sure to run the file_search tool first and use the correct id.",
                retryable: false);
        }

        return await StartDownload(sessionId, searchResultId, itemToDownload.Link, ct);
    }

    protected async Task<JsonNode> Run(string sessionId, string link, string title, CancellationToken ct)
    {
        var id = link.GetHashCode();

        var existing = await client.GetDownloadItem(id, ct);
        if (existing is not null)
        {
            return ToolError.Create(
                ToolError.Codes.AlreadyExists,
                "Download with this link already exists, choose a different link",
                retryable: false);
        }

        var synthetic = new SearchResult
        {
            Id = id,
            Title = title,
            Link = link
        };
        searchResultsManager.Add(sessionId, [synthetic]);

        return await StartDownload(sessionId, id, link, ct);
    }

    private async Task<JsonNode> StartDownload(string sessionId, int id, string link, CancellationToken ct)
    {
        var savePath = $"{pathConfig.BaseDownloadPath}/{id}";
        await client.Download(link, savePath, id, ct);

        trackedDownloadsManager.Add(sessionId, id);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = $"""
                           Download with id {id} started successfully.
                           User will notify you when it is completed."
                           """
        };
    }
}
```

Note: `ToolError` resolves without an explicit using because the file is in `Domain.Tools.Downloads` and `ToolError` lives in the parent `Domain.Tools` namespace. The `using Domain.DTOs;` line is new versus the original file — needed because the link path constructs `new SearchResult { ... }` directly.

### Step 4: Run the tests and verify they pass

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FileDownloadToolTests" -v normal`

Expected: all five tests pass. If any fail, read the failure message and fix the implementation; do NOT modify the tests to match buggy code.

### Step 5: Run the full Domain test suite to confirm no regressions

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.Domain"`

Expected: all existing Domain unit tests still pass (`ResubscribeDownloadsToolTests` and any others), plus the new `FileDownloadToolTests`.

### Step 6: Commit

```bash
git add \
  Domain/Tools/Downloads/FileDownloadTool.cs \
  Tests/Unit/Domain/FileDownloadToolTests.cs
git commit -m "feat(downloads): add link-path overload to FileDownloadTool

Adds Run(sessionId, link, title, ct) overload that hashes the link to
derive an int id (matching JackettSearchClient convention), seeds
ISearchResultsManager with a synthetic SearchResult, and funnels through
a shared StartDownload helper alongside the existing search-id path."
```

---

## Task 2: MCP layer — accept link/title and validate at boundary

**Files:**
- Create: `Tests/Unit/McpServerLibrary/McpFileDownloadToolTests.cs`
- Modify: `McpServerLibrary/McpTools/McpFileDownloadTool.cs`

### Step 1: Write the failing validation tests

Create `Tests/Unit/McpServerLibrary/McpFileDownloadToolTests.cs`:

```csharp
using McpServerLibrary.McpTools;
using Shouldly;

namespace Tests.Unit.McpServerLibrary;

public class McpFileDownloadToolTests
{
    [Fact]
    public void ValidateInputs_BothProvided_ReturnsInvalidArgument()
    {
        var result = McpFileDownloadTool.ValidateInputs(
            searchResultId: 1,
            link: "magnet:?xt=urn:btih:x",
            title: "x");

        result.ShouldNotBeNull();
        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        result["message"]!.GetValue<string>().ShouldContain("either");
    }

    [Fact]
    public void ValidateInputs_NeitherProvided_ReturnsInvalidArgument()
    {
        var result = McpFileDownloadTool.ValidateInputs(
            searchResultId: null,
            link: null,
            title: null);

        result.ShouldNotBeNull();
        result["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
    }

    [Fact]
    public void ValidateInputs_LinkWithoutTitle_ReturnsInvalidArgument()
    {
        var result = McpFileDownloadTool.ValidateInputs(
            searchResultId: null,
            link: "magnet:?xt=urn:btih:x",
            title: null);

        result.ShouldNotBeNull();
        result["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        result["message"]!.GetValue<string>().ShouldContain("title");
    }

    [Fact]
    public void ValidateInputs_LinkWithBlankTitle_ReturnsInvalidArgument()
    {
        var result = McpFileDownloadTool.ValidateInputs(
            searchResultId: null,
            link: "magnet:?xt=urn:btih:x",
            title: "   ");

        result.ShouldNotBeNull();
        result["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
    }

    [Theory]
    [InlineData("ftp://example.com/file.torrent")]
    [InlineData("/local/path/file.torrent")]
    [InlineData("just-some-text")]
    public void ValidateInputs_LinkWithDisallowedPrefix_ReturnsInvalidArgument(string link)
    {
        var result = McpFileDownloadTool.ValidateInputs(
            searchResultId: null,
            link: link,
            title: "Title");

        result.ShouldNotBeNull();
        result["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        result["message"]!.GetValue<string>().ShouldContain("magnet");
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:abc")]
    [InlineData("MAGNET:?xt=urn:btih:abc")]
    [InlineData("http://tracker.example.com/file.torrent")]
    [InlineData("https://tracker.example.com/file.torrent")]
    [InlineData("HTTPS://tracker.example.com/file.torrent")]
    public void ValidateInputs_AcceptedLinkWithTitle_ReturnsNull(string link)
    {
        var result = McpFileDownloadTool.ValidateInputs(
            searchResultId: null,
            link: link,
            title: "Title");

        result.ShouldBeNull();
    }

    [Fact]
    public void ValidateInputs_OnlySearchResultId_ReturnsNull()
    {
        var result = McpFileDownloadTool.ValidateInputs(
            searchResultId: 42,
            link: null,
            title: null);

        result.ShouldBeNull();
    }
}
```

### Step 2: Run the tests and verify they fail

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpFileDownloadToolTests" -v normal`

Expected: all tests fail to compile because `McpFileDownloadTool.ValidateInputs` does not exist yet.

### Step 3: Update `McpFileDownloadTool` with new params, dispatch, and validation

Replace the contents of `McpServerLibrary/McpTools/McpFileDownloadTool.cs` with:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Config;
using Domain.Tools.Downloads;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class McpFileDownloadTool(
    IDownloadClient client,
    ISearchResultsManager searchResultsManager,
    ITrackedDownloadsManager trackedDownloadsManager,
    DownloadPathConfig pathConfig)
    : FileDownloadTool(client, searchResultsManager, trackedDownloadsManager, pathConfig)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        RequestContext<CallToolRequestParams> context,
        [Description("Id from a prior file_search result. Mutually exclusive with link.")]
        int? searchResultId,
        [Description("Magnet URI or http(s) .torrent URL obtained via web tools. Requires title. Mutually exclusive with searchResultId.")]
        string? link,
        [Description("Descriptive title for the download (required when link is provided; ignored otherwise).")]
        string? title,
        CancellationToken cancellationToken)
    {
        var sessionId = context.Server.StateKey;

        var validation = ValidateInputs(searchResultId, link, title);
        if (validation is not null)
        {
            return ToolResponse.Create(validation);
        }

        var result = searchResultId.HasValue
            ? await Run(sessionId, searchResultId.Value, cancellationToken)
            : await Run(sessionId, link!, title!, cancellationToken);

        await context.Server.SendNotificationAsync(
            "notifications/resources/list_changed",
            cancellationToken: cancellationToken);
        return ToolResponse.Create(result);
    }

    internal static JsonNode? ValidateInputs(int? searchResultId, string? link, string? title)
    {
        var hasId = searchResultId.HasValue;
        var hasLink = !string.IsNullOrWhiteSpace(link);

        if (hasId && hasLink)
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Provide either searchResultId or link, not both.",
                retryable: false);
        }

        if (!hasId && !hasLink)
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Provide either searchResultId or link.",
                retryable: false);
        }

        if (hasLink && string.IsNullOrWhiteSpace(title))
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "title is required when link is provided.",
                retryable: false);
        }

        if (hasLink && !IsAcceptedLink(link!))
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "link must start with magnet:, http://, or https://.",
                retryable: false);
        }

        return null;
    }

    private static bool IsAcceptedLink(string link)
    {
        return link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
               || link.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
}
```

### Step 4: Run the tests and verify they pass

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpFileDownloadToolTests" -v normal`

Expected: all 12 cases (4 explicit `[Fact]` + 5 `[Theory]` rows + 1 `[Theory]` row across two methods, totalling 12) pass.

### Step 5: Build the full solution to catch any reference issues

Run: `dotnet build Agent.sln`

Expected: build succeeds with no errors. The `internal static ValidateInputs` is visible to the test project because `Tests` is the only test assembly and the project references the MCP server projects.

If the test project does not see `internal` members (no `InternalsVisibleTo` attribute), promote `ValidateInputs` to `public static` instead — both work; `internal` is preferred so it doesn't appear in the public API surface, but `public` is acceptable. Verify with `grep -n "InternalsVisibleTo" McpServerLibrary/*.csproj McpServerLibrary/Properties/*.cs 2>/dev/null` — if there's no entry, switch the modifier to `public`.

### Step 6: Commit

```bash
git add \
  McpServerLibrary/McpTools/McpFileDownloadTool.cs \
  Tests/Unit/McpServerLibrary/McpFileDownloadToolTests.cs
git commit -m "feat(downloads): McpFileDownloadTool accepts link/title with validation

Adds optional link/title params to download_file. ValidateInputs enforces
XOR (id vs link), required title with link, and an accepted-prefix check
(magnet:, http://, https://). Dispatches to the matching Domain Run
overload after validation."
```

---

## Task 3: Update Captain Agent prompt

**Files:**
- Modify: `Domain/Prompts/DownloaderPrompt.cs`

### Step 1: Locate the insertion point

The new bullet goes inside `AgentSystemPrompt`'s **Phase 1: The Hunt** section, right before the line:

> **The moment a suitable treasure is identified, Phase 1 is over and you MUST proceed immediately to Phase 2.**

That sentence currently lives toward the end of Phase 1 (around line 73 of `DownloaderPrompt.cs`). The new bullet must be the last bullet before that closing sentence.

### Step 2: Insert the new bullet

Use `Edit` to add the bullet by anchoring on the existing "Review Before Re-Searching" bullet (the last bullet currently in Phase 1) and the closing "moment a suitable treasure" sentence. Apply this replacement:

Old string:
```
        *   **Review Before Re-Searching:** If the user requests a different file (e.g., "get a smaller one", "more seeders", "higher quality"), **first look through the search results you already have**. Only search again if none of the existing results satisfy the new criteria.

        **The moment a suitable treasure is identified, Phase 1 is over and you MUST proceed immediately to Phase 2.**
```

New string:
```
        *   **Review Before Re-Searching:** If the user requests a different file (e.g., "get a smaller one", "more seeders", "higher quality"), **first look through the search results you already have**. Only search again if none of the existing results satisfy the new criteria.
        *   **When the Indexers Run Dry:** If your volleys with `file_search` come up empty after exhausting reasonable variations (different separators, looser query, alternate translations of the title), board the open seas. Use `web_search` to find indexer sites or torrent listings, then `web_browse` and `web_snapshot` to drill into a page and extract the magnet URI or `.torrent` URL. When ye find one, pass it directly to the `download_file` tool with the `link` and a descriptive `title` ye gleaned from the page (e.g., the release name with quality and group). The same quality bar from Phase 1 still applies — don't accept low-seeder or wrong-quality booty just because ye found it on the web.

        **The moment a suitable treasure is identified, Phase 1 is over and you MUST proceed immediately to Phase 2.**
```

### Step 3: Build to verify the verbatim string still compiles

Run: `dotnet build Domain/Domain.csproj`

Expected: build succeeds. (Triple-quoted raw string literals don't break on `\` or quotes inside, so the new bullet is safe.)

### Step 4: Commit

```bash
git add Domain/Prompts/DownloaderPrompt.cs
git commit -m "feat(prompts): teach Captain Agent the web-search fallback

When file_search comes up dry, the agent should use web_search +
web_browse + web_snapshot to find a magnet/.torrent URL on an indexer
site, then pass it to download_file with a descriptive title."
```

---

## Task 4: Integration test — link path through real qBittorrent

**Files:**
- Modify: `Tests/Integration/McpServerTests/McpLibraryServerTests.cs`

### Step 1: Add the integration test

Append the following inside the `#region FileDownload Tests` block (after the existing `FileDownloadTool_WithInvalidId_ReturnsError` test, before the `#endregion`):

```csharp
[Fact]
public async Task FileDownloadTool_WithLinkAndTitle_StartsDownloadAndStatusReportsTitle()
{
    // Arrange — use a small, well-seeded public-domain magnet for stability.
    // Sintel (Blender Foundation, Creative Commons) is a long-standing test torrent.
    const string link =
        "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10" +
        "&dn=Sintel" +
        "&tr=udp%3A%2F%2Fexplodie.org%3A6969" +
        "&tr=udp%3A%2F%2Ftracker.coppersurfer.tk%3A6969" +
        "&tr=udp%3A%2F%2Ftracker.empire-js.us%3A1337" +
        "&tr=udp%3A%2F%2Ftracker.leechers-paradise.org%3A6969" +
        "&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337" +
        "&tr=wss%3A%2F%2Ftracker.btorrent.xyz" +
        "&tr=wss%3A%2F%2Ftracker.fastcast.nz" +
        "&tr=wss%3A%2F%2Ftracker.openwebtorrent.com" +
        "&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F";
    const string title = "Sintel Test Title";
    var expectedId = link.GetHashCode();

    var client = await McpClient.CreateAsync(
        new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(fixture.McpEndpoint)
        }),
        cancellationToken: CancellationToken.None);

    try
    {
        // Act — start download via the link path
        var downloadResult = await client.CallToolAsync(
            "download_file",
            new Dictionary<string, object?>
            {
                ["link"] = link,
                ["title"] = title
            },
            cancellationToken: CancellationToken.None);

        // Assert — download_file accepted the link and returned success
        downloadResult.ShouldNotBeNull();
        var downloadContent = GetTextContent(downloadResult);
        downloadContent.ShouldContain("success");

        // Act — query status using the same id
        var statusResult = await client.CallToolAsync(
            "download_status",
            new Dictionary<string, object?>
            {
                ["downloadId"] = expectedId
            },
            cancellationToken: CancellationToken.None);

        // Assert — status carries our supplied title (came from synthetic SearchResult cache)
        var statusContent = GetTextContent(statusResult);
        statusContent.ShouldContain(title);

        // Cleanup — verify the same id flows through cleanup
        var cleanupResult = await client.CallToolAsync(
            "download_cleanup",
            new Dictionary<string, object?>
            {
                ["downloadId"] = expectedId
            },
            cancellationToken: CancellationToken.None);

        var cleanupContent = GetTextContent(cleanupResult);
        cleanupContent.ShouldContain("success");
    }
    finally
    {
        await client.DisposeAsync();
    }
}

[Fact]
public async Task FileDownloadTool_WithBothIdAndLink_ReturnsInvalidArgument()
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
        "download_file",
        new Dictionary<string, object?>
        {
            ["searchResultId"] = 1,
            ["link"] = "magnet:?xt=urn:btih:x",
            ["title"] = "x"
        },
        cancellationToken: CancellationToken.None);

    // Assert
    result.ShouldNotBeNull();
    var content = GetTextContent(result);
    content.ShouldContain("invalid_argument");

    await client.DisposeAsync();
}
```

### Step 2: Run the integration test

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpLibraryServerTests.FileDownloadTool_With" -v normal`

Expected: both new tests pass. The existing `FileDownloadTool_WithInvalidId_ReturnsError` still passes (its body is unchanged; the tool still rejects unknown ids with the same message).

If the magnet test is flaky against the test environment (qBittorrent may take time to fetch metadata), the assertion only checks that `download_file` returned `success` and that the title is reflected in `download_status` — neither requires actual completion of the download.

### Step 3: Run the full integration suite for the Library server to confirm no regressions

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpLibraryServerTests" -v normal`

Expected: all tests in the file pass.

### Step 4: Commit

```bash
git add Tests/Integration/McpServerTests/McpLibraryServerTests.cs
git commit -m "test(integration): cover download_file link path end-to-end

Adds two integration tests against the McpLibraryServerFixture: one that
exercises the link-path happy flow through download_file →
download_status (title round-trips via the synthetic SearchResult) →
download_cleanup, and one that confirms the boundary rejects calls that
provide both searchResultId and link."
```

---

## Final Verification

### Step 1: Full unit + integration build & run

Run: `dotnet test Tests/Tests.csproj`

Expected: every existing test plus the new ones pass.

### Step 2: Quick manual smoke (optional, only if Docker is available)

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build agent mcp-library
```

Confirm the `agent` container starts without errors and `mcp-library` exposes `download_file` with the new schema by tailing logs:

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot logs --tail=50 mcp-library
```

Expected: tool registration log mentions `download_file` and the description includes the new `link`/`title` documentation.

### Step 3: Final sanity check on the branch

Run: `git log --oneline jack-download-link-support ^master`

Expected: the spec commit (`docs: spec for download_file from a link`), the plan commit (this document), and four implementation commits — Domain feat, MCP feat, prompt, integration test — totalling six commits beyond `master`.

---

## Open the PR (when ready)

Only after the user confirms the implementation is complete and verified:

```bash
gh pr create --title "Allow download_file to accept a magnet/torrent link" --body "$(cat <<'EOF'
## Summary
- `download_file` now accepts either `searchResultId` (existing) or `link` + `title` (new web-search fallback path)
- Synthetic `SearchResult` is seeded into the search-results cache so `download_status` reports the supplied title
- `download_cleanup` and `download_resubscribe` need no changes — same int id flows through
- Captain Agent's prompt is updated with a Phase 1 fallback bullet directing it to `web_search` + `web_browse` + `web_snapshot` when Jackett returns nothing

## Test plan
- [ ] `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FileDownloadToolTests"`
- [ ] `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpFileDownloadToolTests"`
- [ ] `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpLibraryServerTests"`
- [ ] Manual smoke: ask the agent for a deliberately obscure title that Jackett won't have, confirm it falls back to `web_search` and ends with a working `download_file(link, title)` call
EOF
)"
```
