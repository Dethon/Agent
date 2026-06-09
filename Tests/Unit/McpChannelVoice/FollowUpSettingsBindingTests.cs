using McpChannelVoice.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class FollowUpSettingsBindingTests
{
    [Fact]
    public void Bind_Defaults_WhenSectionMissing()
    {
        var settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentId"] = "mycroft" })
            .Build()
            .Get<VoiceSettings>()!;

        settings.FollowUp.Enabled.ShouldBeTrue();
        settings.FollowUp.WindowMs.ShouldBe(7000);
        settings.FollowUp.PlaybackTailMs.ShouldBe(400);
        settings.FollowUp.Chime.ShouldBeTrue();
        settings.FollowUp.MaxTurns.ShouldBe(8);
    }

    [Fact]
    public void Bind_OverridesAndPerSatelliteFlag()
    {
        var settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FollowUp:Enabled"] = "false",
                ["FollowUp:WindowMs"] = "5000",
                ["Satellites:kitchen-01:Identity"] = "household",
                ["Satellites:kitchen-01:Room"] = "Kitchen",
                ["Satellites:kitchen-01:FollowUpEnabled"] = "true"
            })
            .Build()
            .Get<VoiceSettings>()!;

        settings.FollowUp.Enabled.ShouldBeFalse();
        settings.FollowUp.WindowMs.ShouldBe(5000);
        settings.Satellites["kitchen-01"].FollowUpEnabled.ShouldBe(true);
    }
}