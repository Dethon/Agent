using McpChannelVoice.Services.Tse;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class TseAuditTrailTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tse-audit-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero));

    private TseAuditTrail Trail(int maxPairs = 3) =>
        new(root, maxPairs, clock, NullLogger<TseAuditTrail>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RecordWritesPairAndMetadata()
    {
        Trail().Record("Dethon", 512.5, 4200, [1, 2], [3, 4]);
        var dir = Directory.GetDirectories(root).ShouldHaveSingleItem();
        Path.GetFileName(dir).ShouldStartWith("20260722-100000");
        Path.GetFileName(dir).ShouldEndWith("-Dethon");
        File.ReadAllBytes(Path.Combine(dir, "mixture.wav")).ShouldBe(new byte[] { 1, 2 });
        File.ReadAllBytes(Path.Combine(dir, "extracted.wav")).ShouldBe(new byte[] { 3, 4 });
        var meta = File.ReadAllText(Path.Combine(dir, "meta.json"));
        meta.ShouldContain("\"speaker\":\"Dethon\"");
        meta.ShouldContain("\"latencyMs\":4200");
    }

    [Fact]
    public void PrunesOldestBeyondCap()
    {
        var trail = Trail(maxPairs: 3);
        for (var i = 0; i < 5; i++)
        {
            trail.Record("Dethon", null, i, [1], [2]);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        var dirs = Directory.GetDirectories(root).Select(Path.GetFileName).Order().ToList();
        dirs.Count.ShouldBe(3);
        dirs[0]!.ShouldStartWith("20260722-100002"); // the two oldest were pruned
    }

    [Fact]
    public void NullDirIsDisabledNoOp()
    {
        var trail = new TseAuditTrail(null, 3, clock, NullLogger<TseAuditTrail>.Instance);
        trail.Record("Dethon", null, 1, [1], [2]);
        Directory.Exists(root).ShouldBeFalse();
    }
}