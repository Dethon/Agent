using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteSessionDismissStashTests
{
    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    [Fact]
    public void TryConsumeDismissedAlert_WithinWindow_ReturnsOnceThenNull()
    {
        var session = Session();
        var now = DateTimeOffset.UtcNow;
        session.NoteDismissedAlert("alarm \"trash\"", now);

        session.TryConsumeDismissedAlert(now.AddSeconds(10)).ShouldBe("alarm \"trash\"");
        session.TryConsumeDismissedAlert(now.AddSeconds(11)).ShouldBeNull(); // single-use
    }

    [Fact]
    public void TryConsumeDismissedAlert_AfterWindow_ReturnsNull()
    {
        var session = Session();
        var now = DateTimeOffset.UtcNow;
        session.NoteDismissedAlert("alarm \"trash\"", now);

        session.TryConsumeDismissedAlert(now.AddSeconds(61)).ShouldBeNull();
    }

    [Fact]
    public void TryConsumeDismissedAlert_NothingStashed_ReturnsNull()
    {
        Session().TryConsumeDismissedAlert(DateTimeOffset.UtcNow).ShouldBeNull();
    }
}