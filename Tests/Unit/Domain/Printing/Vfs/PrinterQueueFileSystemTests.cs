using System.Text;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.Printing;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Printing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Printing.Vfs;

public class PrinterQueueFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "printfs-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FakePrinterClient _printer = new();
    private PrintSpool _spool = null!;
    private PrintQueueCoordinator _coordinator = null!;

    private PrinterQueueFileSystem Build()
    {
        _spool = new PrintSpool(_root, _clock);
        _coordinator = new PrintQueueCoordinator(_spool, _printer, _clock, TimeSpan.FromMilliseconds(500));
        return new PrinterQueueFileSystem(_spool, _printer, _coordinator);
    }

    [Fact]
    public async Task Backend_Contract_ExposesNameAndUnsupportedOps()
    {
        var fs = Build();

        fs.ShouldBeAssignableTo<IFileSystemBackend>();
        fs.FilesystemName.ShouldBe("print-queue");

        var move = await fs.MoveAsync("a.pdf", "b.pdf", CancellationToken.None);
        move.ShouldBeOfType<FsResult<FsMoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var exec = await fs.ExecAsync("a.pdf", "anything", null, CancellationToken.None);
        exec.ShouldBeOfType<FsResult<FsExecResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
    }

    [Fact]
    public async Task StatusJson_IsReadOnly()
    {
        var fs = Build();

        var create = await fs.CreateAsync("status.json", "{}", true, true, CancellationToken.None);
        create.ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var delete = await fs.DeleteAsync("status.json", CancellationToken.None);
        delete.ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var edit = await fs.EditAsync("status.json", new[] { new TextEdit("a", "b") }, CancellationToken.None);
        edit.ShouldBeOfType<FsResult<FsEditResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
    }

    [Fact]
    public async Task Create_QueuesText_Glob_Read_And_StatusReflectIt()
    {
        var fs = Build();

        var create = await fs.CreateAsync("note.txt", "print me", false, true, CancellationToken.None);
        create.ShouldBeOfType<FsResult<FsCreateResult>.Ok>().Value.Status.ShouldBe("queued");

        var glob = (await fs.GlobAsync("/", "*", CancellationToken.None)).ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        glob.Entries.ShouldContain("/note.txt");
        glob.Entries.ShouldContain("/status.json");

        var read = (await fs.ReadAsync("note.txt", null, null, CancellationToken.None)).ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        read.Content.ShouldBe("print me");

        var status = (await fs.ReadAsync("status.json", null, null, CancellationToken.None)).ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        status.Content.ShouldContain("note.txt");
        status.Content.ShouldContain("Queued");
    }

    [Fact]
    public async Task Create_DuplicateName_RequiresOverwrite()
    {
        var fs = Build();
        await fs.CreateAsync("note.txt", "v1", false, true, CancellationToken.None);

        var dup = await fs.CreateAsync("note.txt", "v2", false, true, CancellationToken.None);
        dup.ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error.ErrorCode.ShouldBe("already_exists");

        var ok = await fs.CreateAsync("note.txt", "v2", true, true, CancellationToken.None);
        ok.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        (await fs.ReadAsync("note.txt", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldBe("v2");
    }

    [Fact]
    public async Task Delete_BeforeSubmit_DoesNotPrint()
    {
        var fs = Build();
        await fs.CreateAsync("note.txt", "print me", false, true, CancellationToken.None);

        var delete = await fs.DeleteAsync("note.txt", CancellationToken.None);
        delete.ShouldBeOfType<FsResult<FsRemoveResult>.Ok>();

        _printer.Submissions.ShouldBeEmpty();
        _printer.Canceled.ShouldBeEmpty();
        (await fs.GlobAsync("/", "*", CancellationToken.None)).ShouldBeOfType<FsResult<FsGlobResult>.Ok>()
            .Value.Entries.ShouldNotContain("/note.txt");
    }

    [Fact]
    public async Task Delete_AfterSubmit_CancelsActiveJob()
    {
        var fs = Build();
        await fs.CreateAsync("note.txt", "print me", false, true, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMilliseconds(600));
        await _coordinator.SubmitDueAsync(CancellationToken.None);
        var jobId = (await _spool.GetAsync("note.txt", CancellationToken.None))!.JobId!.Value;

        var delete = await fs.DeleteAsync("note.txt", CancellationToken.None);
        delete.ShouldBeOfType<FsResult<FsRemoveResult>.Ok>();
        _printer.Canceled.ShouldContain(jobId);
    }

    [Fact]
    public async Task ReadAndInfo_BinaryDocument_AreHandled()
    {
        var fs = Build();
        await fs.WriteChunksAsync("scan.pdf", Single(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00, 0x01 }), true, true, CancellationToken.None);

        var read = await fs.ReadAsync("scan.pdf", null, null, CancellationToken.None);
        read.ShouldBeOfType<FsResult<FsReadResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var info = (await fs.InfoAsync("scan.pdf", CancellationToken.None)).ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        info.Exists.ShouldBeTrue();
        info.Size.ShouldBe(6);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Single(byte[] bytes)
    {
        yield return bytes;
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}