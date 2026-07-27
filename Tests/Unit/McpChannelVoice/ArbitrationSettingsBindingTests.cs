using McpChannelVoice.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ArbitrationSettingsBindingTests
{
    [Fact]
    public void Get_ArbitrationAndRmsOffset_BindFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arbitration:Enabled"] = "false",
                ["Arbitration:WindowMs"] = "750",
                ["Arbitration:StealMarginDb"] = "3.5",
                ["Satellites:office:Identity"] = "household",
                ["Satellites:office:Room"] = "Office",
                ["Satellites:office:RmsOffsetDb"] = "-2.5"
            })
            .Build();

        var settings = config.Get<VoiceSettings>()!;

        settings.Arbitration.Enabled.ShouldBeFalse();
        settings.Arbitration.WindowMs.ShouldBe(750);
        settings.Arbitration.StealMarginDb.ShouldBe(3.5);
        settings.Satellites["office"].RmsOffsetDb.ShouldBe(-2.5);
    }

    [Fact]
    public void Defaults_MatchTheSpec()
    {
        var s = new ArbitrationSettings();
        s.Enabled.ShouldBeTrue();
        s.WindowMs.ShouldBe(500);
        s.StealMarginDb.ShouldBe(6);
        s.DetectionLatencyMs.ShouldBe(181);
        s.WakeWordDurationMs.ShouldBe(700);
        s.AlignSlackMs.ShouldBe(250);
        s.QuietGapMs.ShouldBe(400);
        // History must cover the reconstructed wake-word span plus alignment slack and quiet gap.
        s.HistorySpan.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(181 + 700 + 250 + 400));
        new SatelliteConfig { Identity = "x", Room = "y" }.RmsOffsetDb.ShouldBe(0);
    }
}