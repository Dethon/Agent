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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}