using Domain.Prompts;
using McpChannelVoice.McpPrompts;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class VoiceSystemPromptTests
{
    [Fact]
    public void GetVoicePrompt_WithSatellites_IncludesHeadingIdAndRoom()
    {
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["fran-office-01"] = new() { Identity = "household", Room = "Fran's office" },
            ["laura-office-01"] = new() { Identity = "household", Room = "Laura's office" }
        });

        var result = new VoiceSystemPrompt(registry).GetVoicePrompt();

        result.ShouldContain("## Voice satellites");
        result.ShouldContain("- fran-office-01 — Fran's office");
        result.ShouldContain("- laura-office-01 — Laura's office");
    }

    [Fact]
    public void GetVoicePrompt_NoSatellites_ReturnsEmpty()
    {
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>());

        new VoiceSystemPrompt(registry).GetVoicePrompt().ShouldBe(string.Empty);
    }
}