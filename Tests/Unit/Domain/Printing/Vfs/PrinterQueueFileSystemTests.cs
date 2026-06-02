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
        _coordinator = new PrintQueueCoordinator(_spool, _printer, _clock,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        return new PrinterQueueFileSystem(_spool, _printer, _coordinator, "text,jpeg,pwg-raster,urf,pcl");
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
    public async Task Glob_BraceExpansion_MatchesEitherExtension()
    {
        var fs = Build();
        await fs.CreateAsync("report.txt", "hello", false, true, CancellationToken.None);
        await fs.CreateAsync("notes.md", "hi", false, true, CancellationToken.None);

        var glob = (await fs.GlobAsync("/", "*.{txt,md}", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        glob.Entries.ShouldContain("/report.txt");
        glob.Entries.ShouldContain("/notes.md");
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
        // A supported binary format (JPEG) — read-as-text still fails, info still works.
        await fs.WriteChunksAsync("scan.jpg", Single(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 }), true, true, CancellationToken.None);

        var read = await fs.ReadAsync("scan.jpg", null, null, CancellationToken.None);
        read.ShouldBeOfType<FsResult<FsReadResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var info = (await fs.InfoAsync("scan.jpg", CancellationToken.None)).ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        info.Exists.ShouldBeTrue();
        info.Size.ShouldBe(6);
    }

    [Fact]
    public async Task WriteChunks_UnsupportedFormat_IsRejected()
    {
        var fs = Build();
        // PDF is not in the supported set; the backend rejects it rather than spooling unprintable bytes.
        await Should.ThrowAsync<InvalidOperationException>(
            fs.WriteChunksAsync("scan.pdf", Single(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00, 0x01 }), true, true, CancellationToken.None));
    }

    [Fact]
    public async Task Edit_ReplacesText_AndCancelsPriorSubmission()
    {
        var fs = Build();
        await fs.CreateAsync("note.txt", "hello world", false, true, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        await _coordinator.SubmitDueAsync(CancellationToken.None);
        var jobId = (await _spool.GetAsync("note.txt", CancellationToken.None))!.JobId!.Value;

        var edit = await fs.EditAsync("note.txt", new[] { new TextEdit("world", "there") }, CancellationToken.None);
        edit.ShouldBeOfType<FsResult<FsEditResult>.Ok>().Value.TotalOccurrencesReplaced.ShouldBe(1);

        _printer.Canceled.ShouldContain(jobId);
        (await fs.ReadAsync("note.txt", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldBe("hello there");
        (await _spool.GetAsync("note.txt", CancellationToken.None))!.IsSubmitted.ShouldBeFalse();
    }

    [Fact]
    public async Task Copy_DuplicatesDocumentAsNewQueueEntry()
    {
        var fs = Build();
        await fs.CreateAsync("a.txt", "content", false, true, CancellationToken.None);

        var copy = await fs.CopyAsync("a.txt", "b.txt", false, true, CancellationToken.None);
        copy.ShouldBeOfType<FsResult<FsCopyResult>.Ok>();

        (await fs.ReadAsync("b.txt", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldBe("content");
    }

    [Fact]
    public async Task Search_FindsTextAcrossQueuedDocuments()
    {
        var fs = Build();
        await fs.CreateAsync("a.txt", "the quick brown fox", false, true, CancellationToken.None);
        await fs.CreateAsync("b.txt", "lazy dog", false, true, CancellationToken.None);

        var search = (await fs.SearchAsync("quick", false, null, null, "*", 50, 0, VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        search.FilesWithMatches.ShouldBe(1);
        search.Results[0].File.ShouldBe("/a.txt");
    }

    [Fact]
    public async Task FinishedJob_DisappearsFromQueue_OnNextListing()
    {
        var fs = Build();
        await fs.CreateAsync("note.txt", "print me", false, true, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        await _coordinator.SubmitDueAsync(CancellationToken.None);
        var jobId = (await _spool.GetAsync("note.txt", CancellationToken.None))!.JobId!.Value;

        _printer.CompleteJob(jobId);

        // First listing records the absence but keeps the job (debounced); it disappears after the grace.
        (await fs.GlobAsync("/", "*", CancellationToken.None)).ShouldBeOfType<FsResult<FsGlobResult>.Ok>()
            .Value.Entries.ShouldContain("/note.txt");

        _clock.Advance(TimeSpan.FromMilliseconds(600));
        var glob = (await fs.GlobAsync("/", "*", CancellationToken.None)).ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        glob.Entries.ShouldNotContain("/note.txt");
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