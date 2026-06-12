using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using McpServerLibrary.McpTools;
using ModelContextProtocol.Protocol;
using Shouldly;
using static Tests.Unit.Domain.Downloads.Vfs.DownloadFakes;

namespace Tests.Unit.McpServerLibrary;

public class LibraryFsRoutingTests
{
    private readonly FakeDownloadClient _client;
    private readonly RecordingFileSystemClient _fs;
    private readonly DownloadsFileSystem _downloads;

    public LibraryFsRoutingTests()
    {
        _downloads = BuildFileSystem(out _client, out _, out _fs);
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task FsRead_DownloadsFilesystem_ReadsStatus()
    {
        _client.Add(Item(42));
        var tool = new FsReadTool(_downloads);

        var result = await tool.McpRun("42/status.json", null, null, "downloads");

        var text = Text(result);
        text.ShouldContain("id");
        text.ShouldContain("42");
        text.ShouldContain("Download 42");
    }

    [Fact]
    public async Task FsRead_WithoutDownloadsFilesystem_IsUnsupported()
    {
        var tool = new FsReadTool(_downloads);

        var result = await tool.McpRun("anything.txt", null, null, null);

        Text(result).ShouldContain("unsupported_operation");
    }

    [Fact]
    public async Task FsDelete_DownloadsFilesystem_CleansUp()
    {
        _client.Add(Item(42));
        var tool = new FsDeleteTool(_downloads);

        var result = await tool.McpRun("42", "downloads");

        Text(result).ShouldContain("removed");
        _client.CleanedUp.ShouldContain(42);
    }

    [Fact]
    public async Task FsDelete_WithoutDownloadsFilesystem_IsUnsupported()
    {
        var tool = new FsDeleteTool(_downloads);

        var result = await tool.McpRun("42", null);

        Text(result).ShouldContain("unsupported_operation");
    }

    [Fact]
    public async Task FsGlob_DownloadsFilesystem_ListsViaEngine()
    {
        _client.Add(Item(42));
        _client.Add(Item(7));
        var tool = new FsGlobTool(_fs, new LibraryPathConfig("/library"), _downloads);

        var result = await tool.McpRun("**", "/", "downloads");

        Text(result).ShouldContain("/42/status.json");
    }
}