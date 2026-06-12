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