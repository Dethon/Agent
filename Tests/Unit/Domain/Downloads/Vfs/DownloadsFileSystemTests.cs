using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools.Downloads.Vfs;
using Shouldly;
using static Tests.Unit.Domain.Downloads.Vfs.DownloadFakes;

namespace Tests.Unit.Domain.Downloads.Vfs;

public class DownloadsFileSystemTests
{
    private readonly FakeDownloadClient _client;
    private readonly FakeRoutingStore _routing;
    private readonly RecordingFileSystemClient _fs;
    private readonly DownloadsFileSystem _sut;

    public DownloadsFileSystemTests()
    {
        _sut = BuildFileSystem(out _client, out _routing, out _fs);
    }

    [Fact]
    public async Task Contract_NameAndUnsupportedOps()
    {
        _sut.ShouldBeAssignableTo<IFileSystemBackend>();
        _sut.FilesystemName.ShouldBe("downloads");

        var move = await _sut.MoveAsync("42", "7", CancellationToken.None);
        move.ShouldBeOfType<FsResult<FsMoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var exec = await _sut.ExecAsync("42", "anything", null, CancellationToken.None);
        exec.ShouldBeOfType<FsResult<FsExecResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var create = await _sut.CreateAsync("42/status.json", "{}", true, true, CancellationToken.None);
        create.ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var copy = await _sut.CopyAsync("42", "7", false, true, CancellationToken.None);
        copy.ShouldBeOfType<FsResult<FsCopyResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var edit = await _sut.EditAsync("42/status.json", new[] { new TextEdit("a", "b") }, CancellationToken.None);
        edit.ShouldBeOfType<FsResult<FsEditResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
    }

    [Fact]
    public async Task Glob_ListsDownloadDirsAndStatusFiles()
    {
        _client.Add(Item(42));
        _client.Add(Item(7, DownloadState.Completed));

        var all = (await _sut.GlobAsync("/", "**", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        all.Entries.ShouldContain("/42/");
        all.Entries.ShouldContain("/42/status.json");
        all.Entries.ShouldContain("/7/");
        all.Entries.ShouldContain("/7/status.json");

        var statusOnly = (await _sut.GlobAsync("/", "*/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        statusOnly.Entries.ShouldBe(new[] { "/42/status.json", "/7/status.json" }, ignoreOrder: true);
        statusOnly.Entries.ShouldNotContain("/42/");
    }

    [Fact]
    public async Task Read_StatusJson_RendersDownloadState()
    {
        _client.Add(Item(42));

        var read = (await _sut.ReadAsync("42/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        read.Content.ShouldContain("42");
        read.Content.ShouldContain("InProgress");
        read.Content.ShouldContain("Download 42");

        var missing = await _sut.ReadAsync("99/status.json", null, null, CancellationToken.None);
        missing.ShouldBeOfType<FsResult<FsReadResult>.Err>().Error.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public async Task Info_ReportsExistence()
    {
        _client.Add(Item(42));

        var root = (await _sut.InfoAsync("/", CancellationToken.None)).ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        root.Exists.ShouldBeTrue();
        root.IsDirectory.ShouldBe(true);

        var dir = (await _sut.InfoAsync("42", CancellationToken.None)).ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        dir.Exists.ShouldBeTrue();
        dir.IsDirectory.ShouldBe(true);

        var statusFile = (await _sut.InfoAsync("42/status.json", CancellationToken.None)).ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        statusFile.Exists.ShouldBeTrue();
        statusFile.IsDirectory.ShouldBe(false);

        var missing = (await _sut.InfoAsync("99", CancellationToken.None)).ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        missing.Exists.ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_DownloadDir_CleansUpEverything()
    {
        _client.Add(Item(42));
        await _routing.SetAsync(new DownloadRouting
        {
            DownloadId = 42,
            Title = "Download 42",
            Context = new ConversationContext("agent", "conv", "user", new ReplyTarget("library", "conv"))
        }, CancellationToken.None);

        var delete = (await _sut.DeleteAsync("42", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Ok>().Value;
        delete.Status.ShouldBe("removed");

        _client.CleanedUp.ShouldContain(42);
        (await _routing.ListAsync(CancellationToken.None)).ShouldBeEmpty();
        _fs.RemovedDirectories.ShouldContain("/downloads/42");
    }

    [Fact]
    public async Task Delete_StatusFileOrUnknown_IsRejected()
    {
        _client.Add(Item(42));

        var statusDelete = await _sut.DeleteAsync("42/status.json", CancellationToken.None);
        statusDelete.ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var unknownDelete = await _sut.DeleteAsync("99", CancellationToken.None);
        unknownDelete.ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("not_found");
    }
}