using McpChannelVoice.Services.Tse;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class TseAuditTrailTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tse-audit-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero));

    private TseAuditTrail Trail(int maxPairs = 3) =>
        new(_root, maxPairs, _clock, NullLogger<TseAuditTrail>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        else if (File.Exists(_root))
        {
            File.Delete(_root);
        }
    }

    [Fact]
    public void RecordWritesPairAndMetadata()
    {
        Trail().Record("Dethon", 512.5, 4200, [1, 2], [3, 4]);
        var dir = Directory.GetDirectories(_root).ShouldHaveSingleItem();
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
            _clock.Advance(TimeSpan.FromSeconds(1));
        }
        var dirs = Directory.GetDirectories(_root).Select(Path.GetFileName).Order().ToList();
        dirs.Count.ShouldBe(3);
        dirs[0]!.ShouldStartWith("20260722-100002"); // the two oldest were pruned
    }

    [Fact]
    public void NullDirIsDisabledNoOp()
    {
        var trail = new TseAuditTrail(null, 3, _clock, NullLogger<TseAuditTrail>.Instance);
        trail.Record("Dethon", null, 1, [1], [2]);
        Directory.Exists(_root).ShouldBeFalse();
    }

    [Fact]
    public void RecordSwallowsIoFailureAndDoesNotThrow()
    {
        // A real filesystem fault (not a mock): a file already occupies the audit dir's path, so
        // Directory.CreateDirectory inside Record cannot create it. No permission bits involved,
        // so this behaves identically whether the suite runs as root or not.
        File.WriteAllText(_root, "occupies the audit directory path");
        var trail = Trail();
        Should.NotThrow(() => trail.Record("Dethon", null, 1, [1, 2], [3, 4]));
    }

    [Fact]
    public void PruneContinuesPastAFailingDeleteToPruneNewerStaleEntries()
    {
        // Seed three stale pair directories directly, sorted oldest-first by name: A (real), B (a
        // symlink to A), C (real). Pruning A first makes B a dangling symlink, so Directory.Delete
        // throws DirectoryNotFoundException when B's turn comes -- a genuine TOCTOU I/O failure,
        // not a permission check, so it reproduces identically whether the suite runs as root.
        // Without per-entry isolation, that failure aborts the loop and C -- newer than B, still
        // beyond the cap -- is never pruned, letting the ring grow unbounded.
        Directory.CreateDirectory(_root);
        var a = Directory.CreateDirectory(Path.Combine(_root, "20200101-000000-000-A")).FullName;
        var b = Path.Combine(_root, "20200101-000000-001-B");
        Directory.CreateSymbolicLink(b, a);
        var c = Directory.CreateDirectory(Path.Combine(_root, "20200101-000000-002-C")).FullName;

        Trail(maxPairs: 1).Record("Dethon", null, 1, [1], [2]);

        Directory.Exists(a).ShouldBeFalse();
        Directory.Exists(c).ShouldBeFalse();
    }

    [Fact]
    public void RecordSanitizesTraversalShapedSpeakerIntoAuditDirectory()
    {
        // Enough ".." segments to walk out of root and land at Path.GetTempPath()/evil once the
        // unsanitized speaker is glued onto the timestamp prefix and resolved by the filesystem.
        var escapeTarget = Path.Combine(Path.GetTempPath(), "evil");
        try
        {
            Trail().Record("../../../evil", null, 1, [1], [2]);
            Directory.GetDirectories(_root).ShouldHaveSingleItem();
        }
        finally
        {
            if (Directory.Exists(escapeTarget))
            {
                Directory.Delete(escapeTarget, recursive: true);
            }
        }
    }
}