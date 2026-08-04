using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// Which satellite a voice event is about used to be written by hand at twenty call sites, so a
// report could name two of the three fields and forget the last. One stamp owns the triple now.
public class SatelliteIdentityStampTests
{
    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Room = "Kitchen", Identity = "household" });

    [Fact]
    public void About_StampsAllThreeIdentityFieldsOffTheSession()
    {
        var stamped = new VoiceEvent { Metric = VoiceMetric.SttLatencyMs }.About(Session());

        stamped.SatelliteId.ShouldBe("kitchen-01");
        stamped.Room.ShouldBe("Kitchen");
        stamped.Identity.ShouldBe("household");
    }

    [Fact]
    public void About_LeavesEveryOtherFieldAlone()
    {
        var stamped = new VoiceEvent
        {
            Metric = VoiceMetric.SttLatencyMs,
            Outcome = "final",
            DurationMs = 42,
            ConversationId = "conv-1"
        }.About(Session());

        stamped.Metric.ShouldBe(VoiceMetric.SttLatencyMs);
        stamped.Outcome.ShouldBe("final");
        stamped.DurationMs.ShouldBe(42);
        stamped.ConversationId.ShouldBe("conv-1");
    }

    [Fact]
    public void About_TakesTheIdentityValueOnItsOwn()
    {
        // The arbitration handle carries the identity without the session it came from.
        var identity = SatelliteIdentity.Of(Session());

        var stamped = new VoiceEvent { Metric = VoiceMetric.WakeSuppressed }.About(identity);

        stamped.SatelliteId.ShouldBe("kitchen-01");
        stamped.Room.ShouldBe("Kitchen");
        stamped.Identity.ShouldBe("household");
    }
}