using System.Text;
using Infrastructure.Printing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tests.Unit.Infrastructure.Printing;

public class PrintSpoolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "printspool-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

    private PrintSpool Build() => new(_root, _clock);

    [Fact]
    public async Task Write_Then_Get_And_ReadAll_RoundTrips()
    {
        var spool = Build();
        var bytes = Encoding.UTF8.GetBytes("hello world");

        await spool.WriteBytesAsync("a.txt", "text/plain", bytes, 0, true, CancellationToken.None);

        var entry = await spool.GetAsync("a.txt", CancellationToken.None);
        entry.ShouldNotBeNull();
        entry!.FileName.ShouldBe("a.txt");
        entry.ContentType.ShouldBe("text/plain");
        entry.SizeBytes.ShouldBe(bytes.Length);
        entry.IsSubmitted.ShouldBeFalse();

        (await spool.ReadAllBytesAsync("a.txt", CancellationToken.None)).ShouldBe(bytes);
    }

    [Fact]
    public async Task Write_AppendsAtOffset()
    {
        var spool = Build();
        await spool.WriteBytesAsync("a.bin", "application/octet-stream", new byte[] { 1, 2, 3 }, 0, true, CancellationToken.None);
        await spool.WriteBytesAsync("a.bin", "application/octet-stream", new byte[] { 4, 5 }, 3, false, CancellationToken.None);

        (await spool.ReadAllBytesAsync("a.bin", CancellationToken.None)).ShouldBe(new byte[] { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public async Task ReadBytes_ReportsEofAndTotal()
    {
        var spool = Build();
        await spool.WriteBytesAsync("a.bin", "application/octet-stream", new byte[] { 1, 2, 3, 4 }, 0, true, CancellationToken.None);

        var first = await spool.ReadBytesAsync("a.bin", 0, 3, CancellationToken.None);
        first.Bytes.ShouldBe(new byte[] { 1, 2, 3 });
        first.Eof.ShouldBeFalse();
        first.TotalBytes.ShouldBe(4);

        var second = await spool.ReadBytesAsync("a.bin", 3, 3, CancellationToken.None);
        second.Bytes.ShouldBe(new byte[] { 4 });
        second.Eof.ShouldBeTrue();
    }

    [Fact]
    public async Task MarkSubmitted_SetsJobIdAndTimestamp()
    {
        var spool = Build();
        await spool.WriteBytesAsync("a.txt", "text/plain", new byte[] { 1 }, 0, true, CancellationToken.None);

        await spool.MarkSubmittedAsync("a.txt", 42, _clock.GetUtcNow(), CancellationToken.None);

        var entry = await spool.GetAsync("a.txt", CancellationToken.None);
        entry!.JobId.ShouldBe(42);
        entry.IsSubmitted.ShouldBeTrue();
        entry.SubmittedAt.ShouldBe(_clock.GetUtcNow());
    }

    [Fact]
    public async Task List_ReturnsAllEntries_AndRemove_DeletesBytesAndMeta()
    {
        var spool = Build();
        await spool.WriteBytesAsync("a.txt", "text/plain", new byte[] { 1 }, 0, true, CancellationToken.None);
        await spool.WriteBytesAsync("b.txt", "text/plain", new byte[] { 2 }, 0, true, CancellationToken.None);

        (await spool.ListAsync(CancellationToken.None)).Select(e => e.FileName)
            .OrderBy(n => n).ShouldBe(new[] { "a.txt", "b.txt" });

        await spool.RemoveAsync("a.txt", CancellationToken.None);

        (await spool.GetAsync("a.txt", CancellationToken.None)).ShouldBeNull();
        (await spool.ListAsync(CancellationToken.None)).Select(e => e.FileName).ShouldBe(new[] { "b.txt" });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}