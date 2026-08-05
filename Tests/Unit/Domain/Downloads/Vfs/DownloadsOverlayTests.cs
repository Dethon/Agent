using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools;
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
    public async Task Read_StatusJson_RendersStateWithoutSavePath()
    {
        _client.Add(Item(42));

        var read = (await _sut.ReadAsync("downloads/42/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        read.Content.ShouldContain("42");
        read.Content.ShouldContain("InProgress");
        read.Content.ShouldContain("Download 42");
        read.Content.ShouldNotContain("savePath");

        var missing = await _sut.ReadAsync("downloads/99/status.json", CancellationToken.None);
        missing.ShouldBeOfType<FsResult<FsReadResult>.Err>().Error.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public async Task Read_AbsolutePathUnderLibraryRoot_IsNormalized()
    {
        _client.Add(Item(42));

        var read = await _sut.ReadAsync(
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

        // No download owns 99, so the overlay owns nothing at either path: whatever is on disk
        // there is a real file or directory, and the disk backend answers for it.
        (await _sut.TryInfoAsync("downloads/99/status.json", CancellationToken.None)).ShouldBeNull();
        (await _sut.TryInfoAsync("downloads/99", CancellationToken.None)).ShouldBeNull();
        (await _sut.TryInfoAsync("Movies", CancellationToken.None)).ShouldBeNull();
    }

    // The overlay compiled the caller's pattern itself, outside the guard every other mount's glob
    // goes through, so a pattern past the brace-expansion cap left the overlay throwing where the
    // rest of the filesystem answers an envelope. Only the disk half erroring first hid it.
    [Fact]
    public async Task GlobEntries_APatternPastTheBraceCap_IsTheInvalidArgumentEnvelope()
    {
        _client.Add(Item(42));

        var glob = await _sut.GlobEntriesAsync(
            "", string.Concat(Enumerable.Repeat("{a,b}", 12)) + "**", CancellationToken.None);

        glob.ShouldBeOfType<FsResult<IReadOnlyList<string>>.Err>()
            .Error.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
    }

    [Fact]
    public async Task GlobEntries_MatchRootAndBasePathPatterns()
    {
        _client.Add(Item(42));
        _client.Add(Item(7, DownloadState.Completed));

        var all = await Entries("", "**");
        all.ShouldContain("downloads/42/");
        all.ShouldContain("downloads/42/status.json");
        all.ShouldContain("downloads/7/");
        all.ShouldContain("downloads/7/status.json");

        var statusOnly = await Entries("", "downloads/*/status.json");
        statusOnly.ShouldBe(new[] { "downloads/42/status.json", "downloads/7/status.json" }, ignoreOrder: true);

        var dirsOnly = await Entries("", "downloads/*/");
        dirsOnly.ShouldBe(new[] { "downloads/42/", "downloads/7/" }, ignoreOrder: true);

        var based = await Entries("downloads", "*/status.json");
        based.ShouldBe(new[] { "downloads/42/status.json", "downloads/7/status.json" }, ignoreOrder: true);

        var elsewhere = await Entries("Movies", "**");
        elsewhere.ShouldBeEmpty();
    }

    private async Task<IReadOnlyList<string>> Entries(string basePath, string pattern) =>
        (await _sut.GlobEntriesAsync(basePath, pattern, CancellationToken.None))
        .ShouldBeOfType<FsResult<IReadOnlyList<string>>.Ok>().Value;

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

        var delete = (await _sut.TryDeleteAsync("downloads/42", CancellationToken.None))
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

        var delete = (await _sut.TryDeleteAsync("downloads/99", CancellationToken.None))
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

        (await _sut.TryDeleteAsync("downloads/42/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        (await _sut.TryDeleteAsync("Movies/film.mkv", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        (await _sut.TryDeleteAsync("downloads/123", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("not_found");
    }

    // Every one of these used to reach a live download through a spelling the classifier did not
    // recognise: the dotted ones bypassed the refusals entirely (the disk underneath resolves them),
    // and the padded ones cancelled download 42 when the caller named a directory that is not it.
    [Theory]
    [InlineData("downloads/ 42 ")]
    [InlineData("downloads/042")]
    [InlineData("downloads/+42")]
    public async Task Delete_ADirtySpellingOfADownloadId_CancelsNothing(string path)
    {
        _client.Add(Item(42));

        (await _sut.TryDeleteAsync(path, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        _client.CleanedUp.ShouldBeEmpty();
        _fs.RemovedDirectories.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("downloads/./42")]
    [InlineData("downloads/43/../42")]
    public async Task Delete_ADottedSpellingOfADownloadDir_StillCancelsIt(string path)
    {
        _client.Add(Item(42));

        (await _sut.TryDeleteAsync(path, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Ok>();

        _client.CleanedUp.ShouldContain(42);
    }

    [Theory]
    [InlineData("downloads/42")]
    [InlineData("downloads/./42")]
    [InlineData("downloads/42/./payload.mkv")]
    [InlineData("Movies/../downloads/42")]
    [InlineData("downloads")]
    public async Task TouchesActiveDownload_ADottedSpellingOfTheBoundary_StillOverlaps(string path)
    {
        _client.Add(Item(42));

        (await _sut.TouchesActiveDownloadAsync(path, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task TouchesActiveDownload_APathOutsideEveryDownload_DoesNot()
    {
        _client.Add(Item(42));

        (await _sut.TouchesActiveDownloadAsync("Movies/film.mkv", CancellationToken.None)).ShouldBeFalse();
        (await _sut.TouchesActiveDownloadAsync("downloads/7", CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public void IsVirtualPath_DottedAndPaddedSpellings()
    {
        _sut.IsVirtualPath("downloads/42/./status.json").ShouldBeTrue();
        _sut.IsVirtualPath("downloads/43/../42/status.json").ShouldBeTrue();
        _sut.IsVirtualPath("downloads/042/status.json").ShouldBeFalse();
        _sut.IsVirtualPath("downloads/ 42 /status.json").ShouldBeFalse();
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