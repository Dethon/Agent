# Media Downloads Overlay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove `filesystem://downloads` and absorb download semantics into the media filesystem's `downloads/` subtree — a virtual `status.json` per active download plus a guarded delete carrying the cancel/cleanup logic.

**Architecture:** A `DownloadsOverlay` domain class (reworked from `DownloadsFileSystem`) owns the downloads-subtree semantics; the library server's `Fs*Tool`s consult it first and fall through to the shared `Domain/Tools/Files` disk tools. The agent mounts only `/media`; the completion message and all prompts speak `/media/downloads/<id>`.

**Tech Stack:** .NET 10, xUnit + Shouldly, MCP C# SDK, qBittorrent client.

**Spec:** `docs/superpowers/specs/2026-06-12-media-downloads-overlay-design.md`

---

## Context & Conventions (read first)

- **Branch:** work happens on the currently checked-out branch (`subscription-refactor`). NEVER switch branches.
- **.cs files have NO trailing newline.** The pre-commit hook runs `dotnet format` and re-stages whole files (partial staging won't survive).
- **No XML doc comments.** Comments only explain "why". File-scoped namespaces, primary constructors, LINQ over loops.
- **No try/catch in MCP tools** — the server's `AddCallToolFilter` handles exceptions globally.
- **TDD:** every task writes failing tests first and captures the failure output before implementing. For compile-coupled renames, the RED evidence is the build error of the new test code — capture it.
- **Integration tests** (`Tests/Integration/McpServerTests/...`) start real Jackett/QBittorrent testcontainers. In this WSL environment Docker-dependent tests often fail with `DockerUnavailableException` — that's a known baseline (~148 failures), not a regression. If they can't run, verify with `dotnet build` and the unit suites, and say so in the task report.
- **Build/test commands:** `dotnet build agent.sln` and `dotnet test Tests/Tests.csproj --filter "<filter>" -v minimal` from the repo root.
- The path relationship is structural: the compose volumes pin `${DATA_PATH}/downloads` ≡ `<media root>/downloads`. The qBittorrent-facing savePath base (`downloadLocation`, `/downloads`) is *only* what qBittorrent sees; the library's own disk I/O uses `<BaseLibraryPath>/downloads/<id>`.

---

### Task 1: Domain overlay core + library fs-tool routing

The compile-atomic swap: `DownloadsFileSystem` → `DownloadsOverlay`, new path parsing, new shared constants, all eight library fs tools reworked, DI + test fixture registration updated, and all unit tests rewritten.

**Files:**
- Create: `Domain/Tools/Downloads/Vfs/MediaFilesystem.cs`
- Rewrite: `Domain/Tools/Downloads/Vfs/DownloadsPath.cs`
- Create: `Domain/Tools/Downloads/Vfs/DownloadsOverlay.cs`
- Delete: `Domain/Tools/Downloads/Vfs/DownloadsFileSystem.cs`
- Modify: `Domain/Tools/Files/GlobFilesTool.cs` (typed-result split, behavior-preserving)
- Create: `McpServerLibrary/McpTools/LibraryFilesystem.cs`
- Rewrite: `McpServerLibrary/McpTools/FsReadTool.cs`, `FsDeleteTool.cs`, `FsGlobTool.cs`, `FsInfoTool.cs`, `FsMoveTool.cs`, `FsCopyTool.cs`, `FsBlobReadTool.cs`, `FsBlobWriteTool.cs`
- Modify: `McpServerLibrary/Modules/ConfigModule.cs:49`
- Rewrite: `Tests/Unit/Domain/Downloads/Vfs/DownloadsPathTests.cs`, `DownloadFakes.cs`
- Delete: `Tests/Unit/Domain/Downloads/Vfs/DownloadsFileSystemTests.cs`; Create: `DownloadsOverlayTests.cs`
- Rewrite: `Tests/Unit/McpServerLibrary/LibraryFsRoutingTests.cs`
- Modify: `Tests/Integration/Fixtures/McpLibraryServerFixture.cs` (registration + download path under library root)

- [ ] **Step 1: Rewrite the path-parsing tests**

Replace the whole body of `Tests/Unit/Domain/Downloads/Vfs/DownloadsPathTests.cs`:

```csharp
using Domain.Tools.Downloads.Vfs;
using Shouldly;

namespace Tests.Unit.Domain.Downloads.Vfs;

public class DownloadsPathTests
{
    [Theory]
    [InlineData("downloads/42", DownloadNodeKind.DownloadDir, 42)]
    [InlineData("/downloads/42", DownloadNodeKind.DownloadDir, 42)]
    [InlineData("downloads/42/status.json", DownloadNodeKind.StatusFile, 42)]
    [InlineData("/downloads/42/status.json", DownloadNodeKind.StatusFile, 42)]
    [InlineData("", DownloadNodeKind.Other, null)]
    [InlineData("/", DownloadNodeKind.Other, null)]
    [InlineData("downloads", DownloadNodeKind.Other, null)]
    [InlineData("downloads/foo", DownloadNodeKind.Other, null)]
    [InlineData("downloads/42/payload.mkv", DownloadNodeKind.Other, null)]
    [InlineData("Movies/42", DownloadNodeKind.Other, null)]
    [InlineData("../downloads/42", DownloadNodeKind.Other, null)]
    public void Parse_ClassifiesPath_ReturnsKindAndId(string path, DownloadNodeKind kind, int? id)
    {
        var node = DownloadsPath.Parse(path);
        node.Kind.ShouldBe(kind);
        node.Id.ShouldBe(id);
    }
}
```

- [ ] **Step 2: Rework the shared fakes**

Replace `Tests/Unit/Domain/Downloads/Vfs/DownloadFakes.cs` (changes: `BuildOverlay` replaces `BuildFileSystem`, `RecordingFileSystemClient` gains configurable glob results, comment updated):

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;

namespace Tests.Unit.Domain.Downloads.Vfs;

// Shared test doubles for the downloads overlay, the library fs-tool routing, and the
// completion watcher tests. Keep the public surface stable: FakeDownloadClient.Items/CleanedUp,
// FakeRoutingStore.Entries, and RecordingFileSystemClient.RemovedDirectories/GlobResults
// are read by all three test areas.
public static class DownloadFakes
{
    public static DownloadItem Item(int id, DownloadState state = DownloadState.InProgress) => new()
    {
        Id = id,
        Title = $"Download {id}",
        Link = $"magnet:{id}",
        State = state,
        Progress = state == DownloadState.Completed ? 1.0 : 0.5,
        DownSpeed = 1.5,
        UpSpeed = 0.25,
        Eta = 12,
        SavePath = $"/downloads/{id}",
        Size = 1024
    };

    public static DownloadsOverlay BuildOverlay(
        string libraryRoot,
        out FakeDownloadClient client,
        out FakeRoutingStore routing,
        out RecordingFileSystemClient disk)
    {
        client = new FakeDownloadClient();
        routing = new FakeRoutingStore();
        disk = new RecordingFileSystemClient();
        return new DownloadsOverlay(client, routing, disk, new LibraryPathConfig(libraryRoot));
    }

    public sealed class FakeDownloadClient : IDownloadClient
    {
        public List<DownloadItem> Items { get; } = new();
        public List<int> CleanedUp { get; } = new();

        public void Add(DownloadItem item)
        {
            Items.RemoveAll(i => i.Id == item.Id);
            Items.Add(item);
        }

        public Task Cleanup(int id, CancellationToken cancellationToken = default)
        {
            CleanedUp.Add(id);
            Items.RemoveAll(i => i.Id == id);
            return Task.CompletedTask;
        }

        public Task<DownloadItem?> GetDownloadItem(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.FirstOrDefault(i => i.Id == id));

        public Task<IReadOnlyList<DownloadItem>> GetDownloadItems(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DownloadItem>>(Items.ToList());

        public Task Download(string link, string savePath, int id, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class FakeRoutingStore : IDownloadRoutingStore
    {
        public List<DownloadRouting> Entries { get; } = new();

        public Task SetAsync(DownloadRouting routing, CancellationToken ct = default)
        {
            Entries.RemoveAll(r => r.DownloadId == routing.DownloadId);
            Entries.Add(routing);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DownloadRouting>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DownloadRouting>>(Entries.ToList());

        public Task RemoveAsync(int downloadId, CancellationToken ct = default)
        {
            Entries.RemoveAll(r => r.DownloadId == downloadId);
            return Task.CompletedTask;
        }
    }

    public sealed class RecordingFileSystemClient : IFileSystemClient
    {
        public List<string> RemovedDirectories { get; } = new();
        public List<string> GlobResults { get; } = new();

        public Task RemoveDirectory(string path, CancellationToken cancellationToken = default)
        {
            RemovedDirectories.Add(path);
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, string[]>> DescribeDirectory(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Dictionary<string, string[]>());

        public Task<string[]> Glob(string basePath, string pattern, CancellationToken cancellationToken = default) =>
            Task.FromResult(GlobResults.ToArray());

        public Task Move(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveFile(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> MoveToTrash(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(path);
    }
}
```

- [ ] **Step 3: Write the overlay tests**

Delete `Tests/Unit/Domain/Downloads/Vfs/DownloadsFileSystemTests.cs`. Create `Tests/Unit/Domain/Downloads/Vfs/DownloadsOverlayTests.cs`:

```csharp
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools.Downloads.Vfs;
using Shouldly;
using static Tests.Unit.Domain.Downloads.Vfs.DownloadFakes;

namespace Tests.Unit.Domain.Downloads.Vfs;

public class DownloadsOverlayTests : IDisposable
{
    private readonly string _libraryRoot;
    private readonly FakeDownloadClient _client;
    private readonly FakeRoutingStore _routing;
    private readonly RecordingFileSystemClient _fs;
    private readonly DownloadsOverlay _sut;

    public DownloadsOverlayTests()
    {
        _libraryRoot = Path.Combine(Path.GetTempPath(), $"overlay-{Guid.NewGuid()}");
        Directory.CreateDirectory(_libraryRoot);
        _sut = BuildOverlay(_libraryRoot, out _client, out _routing, out _fs);
    }

    public void Dispose()
    {
        if (Directory.Exists(_libraryRoot))
        {
            Directory.Delete(_libraryRoot, true);
        }
    }

    [Fact]
    public async Task TryRead_StatusJson_RendersStateWithoutSavePath()
    {
        _client.Add(Item(42));

        var read = (await _sut.TryReadAsync("downloads/42/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        read.Content.ShouldContain("42");
        read.Content.ShouldContain("InProgress");
        read.Content.ShouldContain("Download 42");
        read.Content.ShouldNotContain("savePath");

        var missing = await _sut.TryReadAsync("downloads/99/status.json", CancellationToken.None);
        missing.ShouldBeOfType<FsResult<FsReadResult>.Err>().Error.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public async Task TryRead_NonOverlayPath_ReturnsNull()
    {
        (await _sut.TryReadAsync("Movies/film.mkv", CancellationToken.None)).ShouldBeNull();
        (await _sut.TryReadAsync("downloads/42/payload.mkv", CancellationToken.None)).ShouldBeNull();
        (await _sut.TryReadAsync("downloads", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task TryRead_AbsolutePathUnderLibraryRoot_IsNormalized()
    {
        _client.Add(Item(42));

        var read = await _sut.TryReadAsync(
            Path.Combine(_libraryRoot, "downloads", "42", "status.json"), CancellationToken.None);
        read.ShouldBeOfType<FsResult<FsReadResult>.Ok>();
    }

    [Fact]
    public async Task TryInfo_OwnsStatusFilesAndLiveDownloadDirs()
    {
        _client.Add(Item(42));

        var dir = (await _sut.TryInfoAsync("downloads/42", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        dir.Exists.ShouldBeTrue();
        dir.IsDirectory.ShouldBe(true);

        var statusFile = (await _sut.TryInfoAsync("downloads/42/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        statusFile.Exists.ShouldBeTrue();
        statusFile.IsDirectory.ShouldBe(false);

        var deadStatus = (await _sut.TryInfoAsync("downloads/99/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        deadStatus.Exists.ShouldBeFalse();

        (await _sut.TryInfoAsync("downloads/99", CancellationToken.None)).ShouldBeNull();
        (await _sut.TryInfoAsync("Movies", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task GlobEntries_MatchRootAndBasePathPatterns()
    {
        _client.Add(Item(42));
        _client.Add(Item(7, DownloadState.Completed));

        var all = await _sut.GlobEntriesAsync("", "**", CancellationToken.None);
        all.ShouldContain("downloads/42/");
        all.ShouldContain("downloads/42/status.json");
        all.ShouldContain("downloads/7/");
        all.ShouldContain("downloads/7/status.json");

        var statusOnly = await _sut.GlobEntriesAsync("", "downloads/*/status.json", CancellationToken.None);
        statusOnly.ShouldBe(new[] { "downloads/42/status.json", "downloads/7/status.json" }, ignoreOrder: true);

        var dirsOnly = await _sut.GlobEntriesAsync("", "downloads/*/", CancellationToken.None);
        dirsOnly.ShouldBe(new[] { "downloads/42/", "downloads/7/" }, ignoreOrder: true);

        var based = await _sut.GlobEntriesAsync("downloads", "*/status.json", CancellationToken.None);
        based.ShouldBe(new[] { "downloads/42/status.json", "downloads/7/status.json" }, ignoreOrder: true);

        var elsewhere = await _sut.GlobEntriesAsync("Movies", "**", CancellationToken.None);
        elsewhere.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_ActiveDownload_CleansUpEverything()
    {
        _client.Add(Item(42));
        await _routing.SetAsync(new DownloadRouting
        {
            DownloadId = 42,
            Title = "Download 42",
            Context = new ConversationContext("agent", "conv", "user", new ReplyTarget("library", "conv"))
        }, CancellationToken.None);

        var delete = (await _sut.DeleteAsync("downloads/42", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Ok>().Value;
        delete.Status.ShouldBe("removed");

        _client.CleanedUp.ShouldContain(42);
        (await _routing.ListAsync(CancellationToken.None)).ShouldBeEmpty();
        _fs.RemovedDirectories.ShouldContain(Path.Combine(_libraryRoot, "downloads", "42"));
    }

    [Fact]
    public async Task Delete_LeftoverDirWithoutTorrent_RemovesDirAndStaleRouting()
    {
        Directory.CreateDirectory(Path.Combine(_libraryRoot, "downloads", "99"));
        await _routing.SetAsync(new DownloadRouting
        {
            DownloadId = 99,
            Title = "Stale",
            Context = new ConversationContext("agent", "conv", "user", new ReplyTarget("library", "conv"))
        }, CancellationToken.None);

        var delete = (await _sut.DeleteAsync("downloads/99", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Ok>().Value;
        delete.Status.ShouldBe("removed");

        _client.CleanedUp.ShouldBeEmpty();
        (await _routing.ListAsync(CancellationToken.None)).ShouldBeEmpty();
        _fs.RemovedDirectories.ShouldContain(Path.Combine(_libraryRoot, "downloads", "99"));
    }

    [Fact]
    public async Task Delete_RejectsNonDownloadTargets()
    {
        _client.Add(Item(42));

        (await _sut.DeleteAsync("downloads/42/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        (await _sut.DeleteAsync("Movies/film.mkv", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        (await _sut.DeleteAsync("downloads/123", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public void IsVirtualPath_TrueOnlyForStatusFiles()
    {
        _sut.IsVirtualPath("downloads/42/status.json").ShouldBeTrue();
        _sut.IsVirtualPath("/downloads/42/status.json").ShouldBeTrue();
        _sut.IsVirtualPath(Path.Combine(_libraryRoot, "downloads", "42", "status.json")).ShouldBeTrue();
        _sut.IsVirtualPath("downloads/42").ShouldBeFalse();
        _sut.IsVirtualPath("downloads/42/file.mkv").ShouldBeFalse();
        _sut.IsVirtualPath("Movies/status.json").ShouldBeFalse();
    }
}
```

- [ ] **Step 4: Write the tool-routing tests**

Replace `Tests/Unit/McpServerLibrary/LibraryFsRoutingTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using McpServerLibrary.McpTools;
using ModelContextProtocol.Protocol;
using Shouldly;
using static Tests.Unit.Domain.Downloads.Vfs.DownloadFakes;

namespace Tests.Unit.McpServerLibrary;

public class LibraryFsRoutingTests : IDisposable
{
    private readonly string _libraryRoot;
    private readonly FakeDownloadClient _client;
    private readonly RecordingFileSystemClient _fs;
    private readonly DownloadsOverlay _overlay;
    private readonly LibraryPathConfig _libraryPath;

    public LibraryFsRoutingTests()
    {
        _libraryRoot = Path.Combine(Path.GetTempPath(), $"library-{Guid.NewGuid()}");
        Directory.CreateDirectory(_libraryRoot);
        _overlay = BuildOverlay(_libraryRoot, out _client, out _, out _fs);
        _libraryPath = new LibraryPathConfig(_libraryRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_libraryRoot))
        {
            Directory.Delete(_libraryRoot, true);
        }
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task FsRead_StatusPath_ReadsStatus()
    {
        _client.Add(Item(42));
        var tool = new FsReadTool(_overlay);

        var result = await tool.McpRun("downloads/42/status.json", null, null, "media");

        var text = Text(result);
        text.ShouldContain("42");
        text.ShouldContain("Download 42");
    }

    [Fact]
    public async Task FsRead_NonStatusPath_IsUnsupported()
    {
        var tool = new FsReadTool(_overlay);

        Text(await tool.McpRun("Movies/film.mkv", null, null, "media")).ShouldContain("unsupported_operation");
        Text(await tool.McpRun("downloads/42/payload.mkv", null, null, null)).ShouldContain("unsupported_operation");
    }

    [Fact]
    public async Task FsRead_UnknownFilesystem_IsRejected()
    {
        var tool = new FsReadTool(_overlay);

        Text(await tool.McpRun("downloads/42/status.json", null, null, "downloads"))
            .ShouldContain("unsupported_operation");
    }

    [Fact]
    public async Task FsDelete_DownloadDir_CleansUp()
    {
        _client.Add(Item(42));
        var tool = new FsDeleteTool(_overlay);

        var result = await tool.McpRun("downloads/42", "media");

        Text(result).ShouldContain("removed");
        _client.CleanedUp.ShouldContain(42);
    }

    [Fact]
    public async Task FsDelete_NonDownloadPath_IsUnsupported()
    {
        var tool = new FsDeleteTool(_overlay);

        Text(await tool.McpRun("Movies/film.mkv", null)).ShouldContain("unsupported_operation");
    }

    [Fact]
    public async Task FsGlob_MergesVirtualEntriesWithDiskResults()
    {
        _client.Add(Item(42));
        _fs.GlobResults.Add($"{_libraryRoot}/downloads/42/");
        _fs.GlobResults.Add($"{_libraryRoot}/downloads/42/payload.mkv");
        var tool = new FsGlobTool(_fs, _libraryPath, _overlay);

        var result = await tool.McpRun("**", "", "media");

        var node = JsonNode.Parse(Text(result))!;
        var entries = node["entries"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        entries.ShouldContain("downloads/42/status.json");
        entries.ShouldContain("downloads/42/payload.mkv");
        entries.Count(e => e == "downloads/42/").ShouldBe(1);
        node["total"]!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public async Task FsMove_VirtualStatusPath_IsRejected()
    {
        var tool = new FsMoveTool(_fs, _libraryPath, _overlay);

        Text(await tool.McpRun("downloads/42/status.json", "Movies/status.json", "media"))
            .ShouldContain("unsupported_operation");
        Text(await tool.McpRun("Movies/film.mkv", "downloads/42/status.json", null))
            .ShouldContain("unsupported_operation");
    }

    [Fact]
    public void FsCopyAndBlobs_VirtualStatusPath_AreRejected()
    {
        var copy = new FsCopyTool(_libraryPath, _overlay);
        var blobRead = new FsBlobReadTool(_libraryPath, _overlay);
        var blobWrite = new FsBlobWriteTool(_libraryPath, _overlay);

        Text(copy.McpRun("downloads/42/status.json", "Movies/x.json", filesystem: "media"))
            .ShouldContain("unsupported_operation");
        Text(blobRead.McpRun("downloads/42/status.json", 0, 1024, "media"))
            .ShouldContain("unsupported_operation");
        Text(blobWrite.McpRun("downloads/42/status.json", "", 0, false, true, "media"))
            .ShouldContain("unsupported_operation");
    }

    [Fact]
    public async Task FsInfo_LiveDownloadDirIsVirtual_OtherPathsFallThroughToDisk()
    {
        _client.Add(Item(42));
        File.WriteAllText(Path.Combine(_libraryRoot, "real.txt"), "x");
        var tool = new FsInfoTool(_libraryPath, _overlay);

        var dir = JsonNode.Parse(Text(await tool.McpRun("downloads/42", "media")))!;
        dir["exists"]!.GetValue<bool>().ShouldBeTrue();
        dir["isDirectory"]!.GetValue<bool>().ShouldBeTrue();

        var file = JsonNode.Parse(Text(await tool.McpRun("real.txt", null)))!;
        file["exists"]!.GetValue<bool>().ShouldBeTrue();
        file["isDirectory"]!.GetValue<bool>().ShouldBeFalse();

        var missing = JsonNode.Parse(Text(await tool.McpRun("downloads/99", "media")))!;
        missing["exists"]!.GetValue<bool>().ShouldBeFalse();
    }
}
```

- [ ] **Step 5: Verify RED**

Run: `dotnet build agent.sln 2>&1 | tail -20`
Expected: FAILS with `CS0246`/`CS0117` errors — `DownloadsOverlay` and `DownloadNodeKind.Other` don't exist yet, `BuildOverlay` undefined. Capture this output as the RED evidence.

- [ ] **Step 6: Create the shared constants**

Create `Domain/Tools/Downloads/Vfs/MediaFilesystem.cs`:

```csharp
namespace Domain.Tools.Downloads.Vfs;

// Single source for the library server's filesystem identity. The compose volumes pin the
// physical identity ${DATA_PATH}/downloads == <media root>/downloads, so the downloads
// subdir is a constant, not configuration.
public static class MediaFilesystem
{
    public const string Name = "media";
    public const string MountPoint = "/media";
    public const string DownloadsSubdir = "downloads";

    public static string AgentDownloadDir(int id) => $"{MountPoint}/{DownloadsSubdir}/{id}";
}
```

- [ ] **Step 7: Rewrite the path parser**

Replace `Domain/Tools/Downloads/Vfs/DownloadsPath.cs`:

```csharp
namespace Domain.Tools.Downloads.Vfs;

public enum DownloadNodeKind
{
    DownloadDir,
    StatusFile,
    Other
}

public sealed record DownloadsNode(DownloadNodeKind Kind, int? Id);

// Classifies media-filesystem paths against the downloads overlay: downloads/<id> is a
// download directory and downloads/<id>/status.json its virtual status file; everything
// else (including payload files inside a download directory) is plain disk territory.
public static class DownloadsPath
{
    public const string StatusFileName = "status.json";

    public static DownloadsNode Parse(string path)
    {
        var segments = (path ?? "").Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments switch
        {
            [MediaFilesystem.DownloadsSubdir, var id] when int.TryParse(id, out var dirId) =>
                new DownloadsNode(DownloadNodeKind.DownloadDir, dirId),
            [MediaFilesystem.DownloadsSubdir, var id, StatusFileName] when int.TryParse(id, out var fileId) =>
                new DownloadsNode(DownloadNodeKind.StatusFile, fileId),
            _ => new DownloadsNode(DownloadNodeKind.Other, null)
        };
    }
}
```

- [ ] **Step 8: Create the overlay, delete the old engine**

Delete `Domain/Tools/Downloads/Vfs/DownloadsFileSystem.cs`. Create `Domain/Tools/Downloads/Vfs/DownloadsOverlay.cs`:

```csharp
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Downloads.Vfs;

// Overlays download semantics on the media filesystem's downloads/ subtree: every active
// download surfaces a virtual read-only downloads/<id>/status.json, and deleting
// downloads/<id> cancels the download and cleans up its files. Payload files inside a
// download directory stay plain disk entries served by the regular media tools, so the
// Try* methods return null for paths the overlay does not own.
public sealed class DownloadsOverlay(
    IDownloadClient downloadClient,
    IDownloadRoutingStore routingStore,
    IFileSystemClient fileSystemClient,
    LibraryPathConfig libraryPath)
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public bool IsVirtualPath(string path) => ParseNode(path).Kind == DownloadNodeKind.StatusFile;

    public async Task<FsResult<FsReadResult>?> TryReadAsync(string path, CancellationToken ct)
    {
        var node = ParseNode(path);
        if (node.Kind != DownloadNodeKind.StatusFile)
        {
            return null;
        }

        var item = await downloadClient.GetDownloadItem(node.Id!.Value, ct);
        if (item is null)
        {
            return new FsResult<FsReadResult>.Err(Error(ToolError.Codes.NotFound, $"Path not found: {path}"));
        }

        var content = RenderStatus(item);
        return new FsResult<FsReadResult>.Ok(new FsReadResult
        {
            FilePath = path,
            Content = content,
            TotalLines = content.Split('\n').Length,
            Truncated = false
        });
    }

    public async Task<FsResult<FsInfoResult>?> TryInfoAsync(string path, CancellationToken ct)
    {
        var node = ParseNode(path);
        switch (node.Kind)
        {
            case DownloadNodeKind.StatusFile:
                {
                    var item = await downloadClient.GetDownloadItem(node.Id!.Value, ct);
                    return new FsResult<FsInfoResult>.Ok(new FsInfoResult
                    {
                        Exists = item is not null,
                        Path = path,
                        IsDirectory = item is not null ? false : null,
                        Size = item is not null ? RenderStatus(item).Length : null
                    });
                }
            case DownloadNodeKind.DownloadDir when await downloadClient.GetDownloadItem(node.Id!.Value, ct) is not null:
                return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = path, IsDirectory = true });
            default:
                return null;
        }
    }

    public async Task<IReadOnlyList<string>> GlobEntriesAsync(string? basePath, string pattern, CancellationToken ct)
    {
        var prefix = (basePath ?? "").Trim('/');
        var items = await downloadClient.GetDownloadItems(ct);

        var dirsOnly = pattern.EndsWith('/');
        var effectivePattern = dirsOnly ? pattern.TrimEnd('/') : pattern;
        var matches = GlobRegex.CompileMatcher(effectivePattern);

        // Candidates are library-root-relative (same convention as the disk glob results);
        // the pattern applies relative to basePath, mirroring the disk matcher root.
        string? Relative(string candidate) =>
            prefix.Length == 0 ? candidate
            : candidate.StartsWith(prefix + "/", StringComparison.Ordinal) ? candidate[(prefix.Length + 1)..]
            : null;

        var dirs = items
            .Select(i => $"{MediaFilesystem.DownloadsSubdir}/{i.Id}")
            .Where(c => Relative(c) is { } rel && matches(rel))
            .Select(c => c + "/");

        if (dirsOnly)
        {
            return dirs.OrderBy(p => p, StringComparer.Ordinal).ToList();
        }

        var files = items
            .Select(i => $"{MediaFilesystem.DownloadsSubdir}/{i.Id}/{DownloadsPath.StatusFileName}")
            .Where(c => Relative(c) is { } rel && matches(rel));

        return dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    public async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        var node = ParseNode(path);
        if (node.Kind == DownloadNodeKind.StatusFile)
        {
            return new FsResult<FsRemoveResult>.Err(Error(ToolError.Codes.UnsupportedOperation, $"{path} is read-only"));
        }

        if (node.Kind != DownloadNodeKind.DownloadDir)
        {
            return new FsResult<FsRemoveResult>.Err(Error(
                ToolError.Codes.UnsupportedOperation,
                $"fs_delete on the media filesystem only removes download directories ({MediaFilesystem.DownloadsSubdir}/<id>)."));
        }

        var id = node.Id!.Value;
        if (await downloadClient.GetDownloadItem(id, ct) is not null)
        {
            // Deliberately best-effort / non-transactional: a Cleanup failure throws and aborts
            // before the housekeeping steps (so we never orphan routing/files for a download
            // that is still running), while the on-disk dir removal is swallowed because
            // leftover/missing files must not undo a successful manager-side cleanup.
            await downloadClient.Cleanup(id, ct);
            await routingStore.RemoveAsync(id, ct);
            await RemoveDownloadDirectoryAsync(id, ct);
            return Removed(path, "Download cancelled and its files removed.");
        }

        if (Directory.Exists(DiskDir(id)))
        {
            // Leftover recovery: no torrent owns the id, but the directory survived a crash or
            // an external removal. Here the dir removal IS the point, so failures propagate.
            await fileSystemClient.RemoveDirectory(DiskDir(id), ct);
            await routingStore.RemoveAsync(id, ct);
            return Removed(path, "Leftover download directory removed.");
        }

        return new FsResult<FsRemoveResult>.Err(Error(ToolError.Codes.NotFound, $"Path not found: {path}"));
    }

    private async Task RemoveDownloadDirectoryAsync(int id, CancellationToken ct)
    {
        try
        {
            await fileSystemClient.RemoveDirectory(DiskDir(id), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Best-effort: a missing or undeletable directory does not undo the cleanup.
        }
    }

    private string DiskDir(int id) =>
        Path.Combine(libraryPath.BaseLibraryPath, MediaFilesystem.DownloadsSubdir, id.ToString());

    // Tools receive mount-relative paths from the agent, but the legacy disk tools also accept
    // absolute paths under the library root — normalize those before classifying.
    private DownloadsNode ParseNode(string path)
    {
        var node = DownloadsPath.Parse(path);
        if (node.Kind != DownloadNodeKind.Other || !Path.IsPathRooted(path))
        {
            return node;
        }

        var root = Path.GetFullPath(libraryPath.BaseLibraryPath);
        var full = Path.GetFullPath(path);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSep, StringComparison.Ordinal)
            ? DownloadsPath.Parse(Path.GetRelativePath(root, full).Replace('\\', '/'))
            : node;
    }

    private static string RenderStatus(DownloadItem item) => JsonSerializer.Serialize(new
    {
        id = item.Id,
        title = item.Title,
        state = item.State.ToString(),
        progressPercent = Math.Round(item.Progress * 100, 2),
        sizeMb = item.Size,
        downSpeedMbps = item.DownSpeed,
        upSpeedMbps = item.UpSpeed,
        etaMinutes = item.Eta
    }, _json);

    private static FsResult<FsRemoveResult> Removed(string path, string message) =>
        new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
        {
            Status = "removed",
            Message = message,
            OriginalPath = path,
            TrashPath = ""
        });

    private static ToolErrorResult Error(string code, string message) =>
        new() { ErrorCode = code, Message = message, Retryable = false };
}
```

- [ ] **Step 9: Split GlobFilesTool into typed core + JSON wrapper**

In `Domain/Tools/Files/GlobFilesTool.cs`, change the cap to `protected` and split `Run` (everything else stays identical):

```csharp
protected const int FileResultCap = 200;

protected async Task<JsonNode> Run(string pattern, CancellationToken cancellationToken, string? basePath = null) =>
    FsResultContract.ToNode(await RunCore(pattern, cancellationToken, basePath));

protected async Task<FsGlobResult> RunCore(string pattern, CancellationToken cancellationToken, string? basePath = null)
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

        var dirsOnly = pattern.EndsWith('/');
        pattern = Path.GetRelativePath(libraryPath.BaseLibraryPath, pattern).TrimEnd('/');
        if (dirsOnly)
        {
            pattern += "/";
        }
    }

    var matcherRoot = ResolveMatcherRoot(basePath);
    var result = await client.Glob(matcherRoot, pattern, cancellationToken);

    // Return entries relative to the mount root (the disk client yields absolute paths). The
    // agent-side VFS tool re-prefixes the mount point, so every filesystem speaks one format.
    var baseRoot = Path.GetFullPath(libraryPath.BaseLibraryPath);
    var relative = result.Select(p => ToMountRelative(baseRoot, p)).ToArray();
    var capped = relative.Length > FileResultCap;

    return new FsGlobResult
    {
        Entries = capped ? relative.Take(FileResultCap).ToArray() : relative,
        Truncated = capped,
        Total = relative.Length
    };
}
```

(Remove the old `private const int FileResultCap = 200;` line. Vault/Sandbox tools keep calling `Run` — behavior unchanged.)

- [ ] **Step 10: Create the tool-level helper**

Create `McpServerLibrary/McpTools/LibraryFilesystem.cs`:

```csharp
using System.Text.Json.Nodes;
using Domain.Tools;
using Domain.Tools.Downloads.Vfs;

namespace McpServerLibrary.McpTools;

// The library server serves a single filesystem ('media'); fs_* calls carry the target
// filesystem name, so anything else is rejected up front.
public static class LibraryFilesystem
{
    public static JsonNode? Reject(string? filesystem) =>
        filesystem is null || filesystem == MediaFilesystem.Name
            ? null
            : ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                $"Unknown filesystem '{filesystem}'. The library server only serves the '{MediaFilesystem.Name}' filesystem.",
                retryable: false);

    public static JsonNode VirtualPathError() =>
        ToolError.Create(
            ToolError.Codes.UnsupportedOperation,
            "status.json is a virtual read-only file; read it with fs_read — it cannot be moved, copied, or written.",
            retryable: false);
}
```

- [ ] **Step 11: Rework the eight fs tools**

`McpServerLibrary/McpTools/FsReadTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools;
using Domain.Tools.Downloads.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsReadTool(DownloadsOverlay downloads)
{
    [McpServerTool(Name = "fs_read")]
    [Description("Read a download's virtual status file (downloads/<id>/status.json — live state, progress, eta). " +
                 "Other media files are not text-readable; use fs_blob_read for raw bytes.")]
    public async Task<CallToolResult> McpRun(
        string path, int? offset = null, int? limit = null, string? filesystem = null,
        CancellationToken ct = default)
    {
        if (LibraryFilesystem.Reject(filesystem) is { } error)
        {
            return ToolResponse.Create(error);
        }

        var overlay = await downloads.TryReadAsync(path, ct);
        return overlay is not null
            ? ToolResponse.Create(overlay)
            : ToolResponse.Create(ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                "fs_read on the media filesystem only reads downloads/<id>/status.json.",
                retryable: false));
    }
}
```

`McpServerLibrary/McpTools/FsDeleteTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Downloads.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsDeleteTool(DownloadsOverlay downloads)
{
    [McpServerTool(Name = "fs_delete")]
    [Description("Delete a download directory (downloads/<id>): cancels the torrent task and cleans up its files. " +
                 "Also removes leftover download directories whose torrent is already gone. " +
                 "Other media paths cannot be deleted.")]
    public async Task<CallToolResult> McpRun(
        string path, string? filesystem = null, CancellationToken ct = default)
    {
        if (LibraryFilesystem.Reject(filesystem) is { } error)
        {
            return ToolResponse.Create(error);
        }

        return ToolResponse.Create(await downloads.DeleteAsync(path, ct));
    }
}
```

`McpServerLibrary/McpTools/FsGlobTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsGlobTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath,
    DownloadsOverlay downloads) : GlobFilesTool(client, libraryPath)
{
    [McpServerTool(Name = "fs_glob")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string pattern,
        string basePath = "",
        string? filesystem = null,
        CancellationToken cancellationToken = default)
    {
        if (LibraryFilesystem.Reject(filesystem) is { } error)
        {
            return ToolResponse.Create(error);
        }

        var disk = await RunCore(pattern, cancellationToken, basePath);
        var virtualEntries = await downloads.GlobEntriesAsync(basePath, pattern, cancellationToken);
        return ToolResponse.Create(FsResultContract.ToNode(Merge(disk, virtualEntries)));
    }

    private static FsGlobResult Merge(FsGlobResult disk, IReadOnlyList<string> virtualEntries)
    {
        var added = virtualEntries.Except(disk.Entries, StringComparer.Ordinal).ToList();
        if (added.Count == 0)
        {
            return disk;
        }

        var combined = disk.Entries.Concat(added).ToList();
        return new FsGlobResult
        {
            Entries = combined.Take(FileResultCap).ToList(),
            Truncated = disk.Truncated || combined.Count > FileResultCap,
            Total = disk.Total + added.Count
        };
    }
}
```

`McpServerLibrary/McpTools/FsInfoTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsInfoTool(LibraryPathConfig libraryPath, DownloadsOverlay downloads)
    : FileInfoTool(libraryPath.BaseLibraryPath)
{
    [McpServerTool(Name = "fs_info")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string path,
        string? filesystem = null,
        CancellationToken ct = default)
    {
        if (LibraryFilesystem.Reject(filesystem) is { } error)
        {
            return ToolResponse.Create(error);
        }

        var overlay = await downloads.TryInfoAsync(path, ct);
        return overlay is not null
            ? ToolResponse.Create(overlay)
            : ToolResponse.Create(Run(path));
    }
}
```

`McpServerLibrary/McpTools/FsMoveTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsMoveTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath,
    DownloadsOverlay downloads) : MoveTool(client, libraryPath)
{
    [McpServerTool(Name = "fs_move")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string sourcePath,
        string destinationPath,
        string? filesystem = null,
        CancellationToken cancellationToken = default)
    {
        if (LibraryFilesystem.Reject(filesystem) is { } error)
        {
            return ToolResponse.Create(error);
        }

        if (downloads.IsVirtualPath(sourcePath) || downloads.IsVirtualPath(destinationPath))
        {
            return ToolResponse.Create(LibraryFilesystem.VirtualPathError());
        }

        return ToolResponse.Create(await Run(sourcePath, destinationPath, cancellationToken));
    }
}
```

`McpServerLibrary/McpTools/FsCopyTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsCopyTool(LibraryPathConfig libraryPath, DownloadsOverlay downloads)
    : CopyTool(libraryPath.BaseLibraryPath)
{
    [McpServerTool(Name = "fs_copy")]
    [Description(Description)]
    public CallToolResult McpRun(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        bool createDirectories = true,
        string? filesystem = null)
    {
        if (LibraryFilesystem.Reject(filesystem) is { } error)
        {
            return ToolResponse.Create(error);
        }

        if (downloads.IsVirtualPath(sourcePath) || downloads.IsVirtualPath(destinationPath))
        {
            return ToolResponse.Create(LibraryFilesystem.VirtualPathError());
        }

        return ToolResponse.Create(Run(sourcePath, destinationPath, overwrite, createDirectories));
    }
}
```

`McpServerLibrary/McpTools/FsBlobReadTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsBlobReadTool(LibraryPathConfig libraryPath, DownloadsOverlay downloads)
    : BlobReadTool(libraryPath.BaseLibraryPath)
{
    [McpServerTool(Name = "fs_blob_read")]
    [Description(Description)]
    public CallToolResult McpRun(
        string path,
        long offset = 0,
        int length = MaxChunkSizeBytes,
        string? filesystem = null)
    {
        if (LibraryFilesystem.Reject(filesystem) is { } error)
        {
            return ToolResponse.Create(error);
        }

        if (downloads.IsVirtualPath(path))
        {
            return ToolResponse.Create(LibraryFilesystem.VirtualPathError());
        }

        return ToolResponse.Create(Run(path, offset, length));
    }
}
```

`McpServerLibrary/McpTools/FsBlobWriteTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsBlobWriteTool(LibraryPathConfig libraryPath, DownloadsOverlay downloads)
    : BlobWriteTool(libraryPath.BaseLibraryPath)
{
    [McpServerTool(Name = "fs_blob_write")]
    [Description(Description)]
    public CallToolResult McpRun(
        string path,
        string contentBase64,
        long offset = 0,
        bool overwrite = false,
        bool createDirectories = true,
        string? filesystem = null)
    {
        if (LibraryFilesystem.Reject(filesystem) is { } error)
        {
            return ToolResponse.Create(error);
        }

        if (downloads.IsVirtualPath(path))
        {
            return ToolResponse.Create(LibraryFilesystem.VirtualPathError());
        }

        return ToolResponse.Create(Run(path, contentBase64, offset, overwrite, createDirectories));
    }
}
```

- [ ] **Step 12: Update DI registrations**

In `McpServerLibrary/Modules/ConfigModule.cs` line 49, replace:

```csharp
            .AddSingleton<DownloadsFileSystem>()
```

with:

```csharp
            .AddSingleton<DownloadsOverlay>()
```

In `Tests/Integration/Fixtures/McpLibraryServerFixture.cs`, change the download path to live inside the library root (lines 38–39) and the registration (line 74):

```csharp
        LibraryPath = Path.Combine(Path.GetTempPath(), $"mcp-library-{Guid.NewGuid()}");
        DownloadPath = Path.Combine(LibraryPath, "downloads");
```

```csharp
            .AddSingleton<DownloadsOverlay>()
```

- [ ] **Step 13: Verify GREEN — build and run the unit suites**

Run: `dotnet build agent.sln 2>&1 | tail -5`
Expected: Build succeeded.

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.Domain.Downloads.Vfs" -v minimal`
Expected: PASS — DownloadsPathTests (11 cases) + DownloadsOverlayTests (9 tests).

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~LibraryFsRoutingTests" -v minimal`
Expected: PASS — 10 tests.

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~DownloadCompletionWatcherTests|FullyQualifiedName~FileDownloadToolTests|FullyQualifiedName~McpFileDownloadToolMetaTests" -v minimal`
Expected: PASS — neighbors that share the fakes/tooling are unaffected.

- [ ] **Step 14: Commit**

```bash
git add -A
git commit -m "refactor: downloads VFS becomes an overlay on the media filesystem"
```

---

### Task 2: Single filesystem resource + integration journey rework

Remove `filesystem://downloads` from the server surface and re-pin the end-to-end journey on the media idiom.

**Files:**
- Modify: `McpServerLibrary/McpResources/FileSystemResource.cs`
- Modify: `Tests/Integration/McpServerTests/McpLibraryServerTests.cs`

- [ ] **Step 1: Add the single-resource test and rework the journey test**

In `Tests/Integration/McpServerTests/McpLibraryServerTests.cs`, add after `McpServer_IsAccessible_ReturnsAllTools`:

```csharp
    [Fact]
    public async Task McpServer_ExposesSingleMediaFilesystemResource()
    {
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        var resources = await client.ListResourcesAsync(cancellationToken: CancellationToken.None);
        var fsResources = resources.Where(r => r.Uri.StartsWith("filesystem://")).ToList();

        fsResources.ShouldHaveSingleItem().Uri.ShouldBe("filesystem://media");

        await client.DisposeAsync();
    }
```

Replace the body of `DownloadFile_WithConversationContextMeta_RecordsRoutingAndServesDownloadsVfs` from the `// Assert - the download is visible through the downloads VFS` comment down to the final routing assertion (the arrange/download/routing-snapshot part stays unchanged), and rename the test:

```csharp
    [Fact]
    public async Task DownloadFile_WithConversationContextMeta_RecordsRoutingAndServesMediaOverlay()
    {
        // Arrange
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        var downloadTool = (await client.ListToolsAsync()).Single(t => t.Name == "download_file");
        var context = new ConversationContext("jack", "conv-journey", "fran", new ReplyTarget("signalr", "conv-journey"));
        var meta = new JsonObject
        {
            ["conversationContext"] = JsonSerializer.SerializeToNode(context, ChannelProtocol.SerializerOptions)
        };
        const string magnetLink =
            "magnet:?xt=urn:btih:KRWPCX3SJUM4IMM4YF3MVSJIBFTHVFCS&dn=ubuntu-24.04-desktop-amd64.iso";

        // Act - download_file with _meta carrying the conversation context
        var downloadResult = await downloadTool.WithMeta(meta).CallAsync(
            new Dictionary<string, object?>
            {
                ["searchResultId"] = null,
                ["link"] = magnetLink,
                ["title"] = "Journey Test"
            },
            cancellationToken: CancellationToken.None);

        // Assert - the routing snapshot points back at the origin conversation
        GetTextContent(downloadResult).ShouldContain("success");
        var routing = (await fixture.RoutingStore.ListAsync()).ShouldHaveSingleItem();
        routing.Title.ShouldBe("Journey Test");
        routing.Context.ConversationId.ShouldBe("conv-journey");
        routing.Context.Origin.ChannelId.ShouldBe("signalr");
        var id = routing.DownloadId;

        // Assert - the download is visible through the media filesystem's downloads overlay
        var globResult = await client.CallToolAsync(
            "fs_glob",
            new Dictionary<string, object?>
            {
                ["pattern"] = "**",
                ["basePath"] = "downloads",
                ["filesystem"] = "media"
            },
            cancellationToken: CancellationToken.None);
        GetTextContent(globResult).ShouldContain($"downloads/{id}/status.json");

        var readResult = await client.CallToolAsync(
            "fs_read",
            new Dictionary<string, object?>
            {
                ["path"] = $"downloads/{id}/status.json",
                ["filesystem"] = "media"
            },
            cancellationToken: CancellationToken.None);
        GetTextContent(readResult).ShouldContain(id.ToString());

        // Assert - the removed 'downloads' filesystem name is rejected
        var staleResult = await client.CallToolAsync(
            "fs_read",
            new Dictionary<string, object?>
            {
                ["path"] = $"downloads/{id}/status.json",
                ["filesystem"] = "downloads"
            },
            cancellationToken: CancellationToken.None);
        GetTextContent(staleResult).ShouldContain("unsupported_operation");

        // Act - deleting the download dir cancels the torrent and drops the routing entry
        var deleteResult = await client.CallToolAsync(
            "fs_delete",
            new Dictionary<string, object?>
            {
                ["path"] = $"downloads/{id}",
                ["filesystem"] = "media"
            },
            cancellationToken: CancellationToken.None);

        // Assert
        GetTextContent(deleteResult).ShouldContain("removed");
        (await fixture.RoutingStore.ListAsync()).ShouldBeEmpty();

        await client.DisposeAsync();
    }
```

- [ ] **Step 2: Verify RED**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpLibraryServerTests" -v minimal`
Expected: `McpServer_ExposesSingleMediaFilesystemResource` FAILS (two `filesystem://` resources); the journey test PASSES (the tool behavior already changed in Task 1). **If the run aborts with `DockerUnavailableException`, note it, verify the build compiles, and continue — the resource change is also covered by the fixture-served tests once Docker is available.**

- [ ] **Step 3: Remove the downloads resource, document the overlay in the media description**

Replace `McpServerLibrary/McpResources/FileSystemResource.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using Domain.Tools.Downloads.Vfs;
using McpServerLibrary.Settings;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpResources;

[McpServerResourceType]
public class FileSystemResource(McpSettings settings)
{
    [McpServerResource(
        UriTemplate = "filesystem://media",
        Name = "Media Filesystem",
        MimeType = "application/json")]
    [Description("Media library filesystem")]
    public string GetMediaInfo()
    {
        return JsonSerializer.Serialize(new
        {
            name = MediaFilesystem.Name,
            mountPoint = MediaFilesystem.MountPoint,
            description = $"Media library ({settings.BaseLibraryPath}) — books, audiobooks, and other downloaded media. " +
                          "Read/list focused; treat writes as organisational only. Does NOT support fs_exec. " +
                          $"Active downloads live under {MediaFilesystem.MountPoint}/{MediaFilesystem.DownloadsSubdir}/<id>/: " +
                          "a virtual read-only status.json reports live state/progress/eta, and deleting the <id> " +
                          "directory cancels the download and cleans up its files."
        });
    }
}
```

- [ ] **Step 4: Verify GREEN**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpLibraryServerTests" -v minimal`
Expected: PASS (or DockerUnavailableException baseline — then `dotnet build agent.sln` must succeed).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: media filesystem absorbs the downloads surface; drop filesystem://downloads"
```

---

### Task 3: Completion message + download_file status hint speak the media path

**Files:**
- Modify: `McpServerLibrary/Services/DownloadCompletionPlanner.cs`
- Modify: `McpServerLibrary/Services/DownloadCompletionWatcher.cs:63`
- Modify: `Domain/Tools/Downloads/FileDownloadTool.cs` (no-context message)
- Modify: `Tests/Unit/McpServerLibrary/DownloadCompletionPlannerTests.cs`
- Modify: `Tests/Unit/Domain/FileDownloadToolTests.cs:252`

- [ ] **Step 1: Update the planner test**

Replace the body of `Tests/Unit/McpServerLibrary/DownloadCompletionPlannerTests.cs` (the `DownloadItem` arrangement disappears — the payload no longer reads qBittorrent's savePath):

```csharp
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpServerLibrary.Services;
using Shouldly;
using Xunit;

namespace Tests.Unit.McpServerLibrary;

public class DownloadCompletionPlannerTests
{
    [Fact]
    public void BuildPayload_TargetsTheOriginatingConversation()
    {
        var routing = new DownloadRouting
        {
            DownloadId = 42,
            Title = "The Lost City of Z 1080p",
            Context = new ConversationContext("jack", "conv-7", "fran", new ReplyTarget("signalr", "conv-7"))
        };

        var payload = DownloadCompletionPlanner.BuildPayload(routing);

        payload.ConversationId.ShouldBe("conv-7");
        payload.AgentId.ShouldBe("jack");
        payload.Sender.ShouldBe("fran");
        payload.ReplyTo.ShouldBe([new ReplyTarget("signalr", "conv-7")]);
        payload.Origin.ShouldBe(new MessageOrigin(MessageOriginKind.Download, null));
        payload.Content.ShouldContain("The Lost City of Z 1080p");
        payload.Content.ShouldContain("/media/downloads/42");
    }
}
```

In `Tests/Unit/Domain/FileDownloadToolTests.cs` line 252, change:

```csharp
        result["message"]!.GetValue<string>().ShouldContain("/downloads");
```

to:

```csharp
        result["message"]!.GetValue<string>().ShouldContain("/media/downloads");
```

- [ ] **Step 2: Verify RED**

Run: `dotnet build agent.sln 2>&1 | tail -10`
Expected: FAILS — `BuildPayload` has no single-argument overload yet (CS1501/CS7036 in the planner test). Capture as RED evidence.

- [ ] **Step 3: Implement**

Replace `McpServerLibrary/Services/DownloadCompletionPlanner.cs`:

```csharp
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Tools.Downloads.Vfs;

namespace McpServerLibrary.Services;

public static class DownloadCompletionPlanner
{
    public static ChannelMessageNotification BuildPayload(DownloadRouting routing) => new()
    {
        ConversationId = routing.Context.ConversationId,
        Sender = routing.Context.UserId,
        Content = BuildPrompt(routing),
        AgentId = routing.Context.AgentId,
        ReplyTo = [routing.Context.Origin],
        Origin = new MessageOrigin(MessageOriginKind.Download, null),
        Timestamp = DateTimeOffset.UtcNow
    };

    private static string BuildPrompt(DownloadRouting routing) =>
        $"""
         [download-complete] Download '{routing.Title}' (id {routing.DownloadId}) has finished downloading to {MediaFilesystem.AgentDownloadDir(routing.DownloadId)}.
         Inform the user their download is ready and carry out any follow-up steps you promised for it (e.g. organizing it into the library).
         """;
}
```

In `McpServerLibrary/Services/DownloadCompletionWatcher.cs` line 63, change:

```csharp
            if (!await emitter.EmitAsync(DownloadCompletionPlanner.BuildPayload(entry, item), ct))
```

to:

```csharp
            if (!await emitter.EmitAsync(DownloadCompletionPlanner.BuildPayload(entry), ct))
```

In `Domain/Tools/Downloads/FileDownloadTool.cs`, add `using Domain.Tools.Downloads.Vfs;` to the usings and change the no-context message:

```csharp
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = $"Download with id {id} started successfully. No conversation context was provided, " +
                          $"so no completion alert will fire; check {MediaFilesystem.MountPoint}/{MediaFilesystem.DownloadsSubdir} for status."
        };
```

- [ ] **Step 4: Verify GREEN**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~DownloadCompletionPlannerTests|FullyQualifiedName~DownloadCompletionWatcherTests|FullyQualifiedName~FileDownloadToolTests" -v minimal`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: completion message reports the media-filesystem download path"
```

---

### Task 4: Prompt, README, compose — one namespace everywhere

**Files:**
- Modify: `Domain/Prompts/DownloaderPrompt.cs`
- Modify: `README.md:17`, `README.md:187`, `README.md:505`
- Modify: `DockerCompose/docker-compose.yml:189`

No unit tests — these are prose/config. Verification is build + grep + the full suite sweep.

- [ ] **Step 1: Update DownloaderPrompt**

In `Domain/Prompts/DownloaderPrompt.cs`, make these exact edits inside `AgentSystemPrompt`:

Phase 3 intro (currently line 102):

```
OLD: When a download finishes, a `[download-complete]` message arrives in this conversation telling you the download id and its location. **DO NOT** attempt to organize a file before that message arrives. You can check progress at any time by reading `/downloads/<id>/status.json`.
NEW: When a download finishes, a `[download-complete]` message arrives in this conversation telling you the download id and its location. **DO NOT** attempt to organize a file before that message arrives. You can check progress at any time by reading `/media/downloads/<id>/status.json`.
```

After the "Leave the Dross" bullet (currently line 117), add a new bullet at the same indentation:

```
            *   **Ignore the Ship's Log:** `status.json` inside a download's directory is a virtual, read-only file — read it for progress, but never move or copy it. It disappears on its own when the download is cleaned up.
```

Phase 4 cleanup bullet (currently line 127):

```
OLD:        *   **Clean Up:** Delete the download's directory in the downloads filesystem (`fs_delete` on `/downloads/<id>`). This removes the torrent task and any leftover files in the download directory in one step.
NEW:        *   **Clean Up:** Delete the download's directory (`fs_delete` on `/media/downloads/<id>`). This removes the torrent task and any leftover files in the download directory in one step.
```

Status report (currently line 141):

```
OLD: Get this by globbing `/downloads/*/status.json` and reading each file.
NEW: Get this by globbing `/media/downloads/*/status.json` and reading each file.
```

Cancellation (currently line 142):

```
OLD: This means deleting `/downloads/<id>` for every task in progress.
NEW: This means deleting `/media/downloads/<id>` for every task in progress.
```

- [ ] **Step 2: Update README**

Line 17:

```
OLD: - **Downloads Virtual Filesystem** - In-flight downloads are exposed as `filesystem://downloads` (mounted at `/downloads`): `/downloads/<id>/status.json` reports live progress, and deleting a download's directory cancels it and cleans up its files
NEW: - **Downloads Overlay** - In-flight downloads surface inside the media filesystem: a virtual `/media/downloads/<id>/status.json` reports live progress, and deleting `/media/downloads/<id>` cancels the download and cleans up its files
```

Line 187 (the `mcp-library` row): replace the resources cell `` `filesystem://media`, `filesystem://downloads` `` with `` `filesystem://media` `` (rest of the row unchanged).

Line 505:

```
OLD: - **Download Tracking** - Completion alerts arrive automatically in the originating conversation (routing snapshots stored in Redis survive restarts); live status is readable anytime via `/downloads/<id>/status.json`
NEW: - **Download Tracking** - Completion alerts arrive automatically in the originating conversation (routing snapshots stored in Redis survive restarts); live status is readable anytime via `/media/downloads/<id>/status.json`
```

- [ ] **Step 3: Drop the redundant volume mount**

In `DockerCompose/docker-compose.yml`, under the `mcp-library` service (line 189), delete the line:

```yaml
      - ${DATA_PATH:-./volumes/data}/downloads:/downloads
```

(The `- ${DATA_PATH:-./volumes/data}:/media` line stays; `qbittorrent`'s own `/downloads` mount at line 39 stays — qBittorrent still sees savePath `/downloads/<id>`. `downloadLocation` in `McpServerLibrary/appsettings.json` is unchanged: it is the qBittorrent-facing savePath base only.)

- [ ] **Step 4: Sweep for stale references**

Run: `grep -rn "filesystem://downloads" --include="*.cs" --include="*.md" --include="*.yml" --include="*.json" . | grep -v "docs/superpowers\|bin/\|obj/"`
Expected: no matches.

Run: `grep -rn "fs_delete\` on \`/downloads\|globbing \`/downloads\|reading \`/downloads" Domain/ README.md`
Expected: no matches.

- [ ] **Step 5: Full verification sweep**

Run: `dotnet build agent.sln 2>&1 | tail -3`
Expected: Build succeeded, 0 errors.

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E" -v minimal 2>&1 | tail -15`
Expected: all unit tests pass. **Known baseline:** ~148 Docker-dependent integration failures (`DockerUnavailableException`) are pre-existing in this WSL environment — compare the failure list against that baseline; only non-Docker failures count as regressions. (`dotnet format --verify-no-changes` is also permanently dirty on top-level `Program.cs` files — baseline, not a regression.)

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "docs/config: unified /media/downloads idiom in prompt, README, compose"
```

---

## Spec coverage map (self-review)

| Spec section | Tasks |
|---|---|
| §1 status.json read (no savePath), info, glob merge, move/copy/blob guards, delete ×3 | Task 1 |
| §1 read policy / unknown-filesystem rejection | Task 1 |
| §2 DownloadsOverlay structure, DownloadsPath reparse, GlobFilesTool typed split, per-tool routing | Task 1 |
| §3 constants (no config knob), library disk I/O under `<BaseLibraryPath>/downloads`, compose mount drop | Tasks 1 (code) + 4 (compose) |
| §4 completion message `/media/downloads/<id>` via shared constants | Task 3 |
| §5 prompt, resource description, fs_read/fs_delete descriptions, README | Tasks 1 (descriptions), 2 (resource), 4 (prompt/README) |
| §6 error handling (vanished torrent, best-effort cleanup, leftover failures propagate, unknown fs) | Task 1 |
| §7 testing (overlay, path, routing, integration single-resource + journey, planner) | Tasks 1–3 |
