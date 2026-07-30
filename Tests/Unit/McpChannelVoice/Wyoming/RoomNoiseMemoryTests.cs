using McpChannelVoice.Services.WyomingProtocol;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Wyoming;

public class RoomNoiseMemoryTests
{
    private static RoomNoiseMemory Memory(FakeTimeProvider time, int samples = 5, int retentionSeconds = 600) =>
        new(time, samples, TimeSpan.FromSeconds(retentionSeconds));

    [Fact]
    public void Rms_NothingRecorded_IsNull()
    {
        Memory(new FakeTimeProvider()).Rms.ShouldBeNull();
    }

    [Fact]
    public void Rms_SeveralSamples_IsTheQuietestOne()
    {
        // The quietest recent background is the only defensible ceiling for a capture's own
        // floor: any sample can be inflated by speech the gate misread, none can be too quiet
        // for a room that was measured that quiet.
        var memory = Memory(new FakeTimeProvider());

        memory.Record(400);
        memory.Record(71);
        memory.Record(180);

        memory.Rms.ShouldBe(71);
    }

    [Fact]
    public void Rms_SamplesOlderThanRetention_AreForgotten()
    {
        // A room changes: music starts, a fan comes on. A stale quiet reading must not keep
        // capping the floor once it stops describing the room.
        var time = new FakeTimeProvider();
        var memory = Memory(time, retentionSeconds: 600);
        memory.Record(71);

        time.Advance(TimeSpan.FromSeconds(601));

        memory.Rms.ShouldBeNull();
    }

    [Fact]
    public void Rms_MoreSamplesThanTheCap_KeepsTheMostRecent()
    {
        var memory = Memory(new FakeTimeProvider(), samples: 3);

        memory.Record(50);
        memory.Record(300);
        memory.Record(310);
        memory.Record(320);

        memory.Rms.ShouldBe(300);
    }

    [Fact]
    public void Record_NonPositiveSample_IsIgnored()
    {
        // Captures that never accumulated a trailing run report 0, which is an absence of a
        // measurement rather than a silent room — recording it would pin the room at silence.
        var memory = Memory(new FakeTimeProvider());
        memory.Record(71);

        memory.Record(0);

        memory.Rms.ShouldBe(71);
    }
}