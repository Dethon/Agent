using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// The per-satellite voice fallback used to be spelled out at four call sites. One of them could have
// been missed without anything failing, so the rule lives here and is tested here.
public class SatelliteSessionVoiceTests
{
    private static readonly VoiceSettings _settings = new()
    {
        Tts = new TtsSettings { OpenAi = new OpenAiTtsConfig { Voice = "em_santa" } }
    };

    private static SatelliteSession Session(TtsOverrides? tts) =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen", Tts = tts });

    [Fact]
    public void ResolveVoice_SatelliteConfiguresItsOwnVoice_UsesIt()
    {
        var session = Session(new TtsOverrides { OpenAi = new OpenAiTtsOverrides { Voice = "ef_dora" } });

        session.ResolveVoice(_settings).ShouldBe("ef_dora");
    }

    [Fact]
    public void ResolveVoice_SatelliteHasNoTtsSection_FallsBackToTheGlobalVoice()
    {
        Session(null).ResolveVoice(_settings).ShouldBe("em_santa");
    }

    [Fact]
    public void ResolveVoice_SatelliteSectionNamesNoVoice_FallsBackToTheGlobalVoice()
    {
        // The override record exists (the satellite has other Tts config, or an env var created the
        // section) but names no voice — that is not a request to be silent about the global one.
        var session = Session(new TtsOverrides { OpenAi = new OpenAiTtsOverrides { Voice = null } });

        session.ResolveVoice(_settings).ShouldBe("em_santa");
    }
}