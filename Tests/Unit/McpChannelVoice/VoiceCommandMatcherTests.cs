using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class VoiceCommandMatcherTests
{
    private static VoiceCommandMatcher Build(bool enabled = true) =>
        new(new CommandSettings
        {
            Enabled = enabled,
            Phrases = new CommandPhrases
            {
                LocalVolumeUp = ["sube el volumen local", "sube el altavoz"],
                LocalVolumeDown = ["baja el volumen local"],
                LocalMute = ["silencia el altavoz"],
                LocalUnmute = ["quita el silencio local"]
            }
        });

    [Fact]
    public void Match_ExactPhrase_ReturnsCommand()
    {
        Build().Match("sube el volumen local").ShouldBe(VoiceCommand.LocalVolumeUp);
        Build().Match("baja el volumen local").ShouldBe(VoiceCommand.LocalVolumeDown);
        Build().Match("silencia el altavoz").ShouldBe(VoiceCommand.LocalMute);
        Build().Match("quita el silencio local").ShouldBe(VoiceCommand.LocalUnmute);
    }

    [Fact]
    public void Match_SecondAliasForSameCommand_ReturnsCommand()
    {
        Build().Match("sube el altavoz").ShouldBe(VoiceCommand.LocalVolumeUp);
    }

    // Whisper emits accents, casing and trailing punctuation; the configured phrases are written
    // plain. Both sides go through the same normalization so config stays readable.
    [Fact]
    public void Match_DifferentCaseAccentsAndPunctuation_StillMatches()
    {
        Build().Match("¡Sube el volumen LOCAL!").ShouldBe(VoiceCommand.LocalVolumeUp);
        Build().Match("  sube   el  volumen   local  ").ShouldBe(VoiceCommand.LocalVolumeUp);
        Build().Match("Silencia el altavóz.").ShouldBe(VoiceCommand.LocalMute);
    }

    // The whole point of whole-transcript matching: a compound request belongs to the agent, not
    // to the fast-path, or the rest of the sentence is silently thrown away.
    [Fact]
    public void Match_CommandEmbeddedInALongerSentence_ReturnsNull()
    {
        Build().Match("sube el volumen local y apaga la luz").ShouldBeNull();
        Build().Match("puedes sube el volumen local").ShouldBeNull();
    }

    [Fact]
    public void Match_UnknownPhrase_ReturnsNull()
    {
        Build().Match("que hora es").ShouldBeNull();
        Build().Match("sube el volumen").ShouldBeNull(); // no local marker: this is Music Assistant
    }

    [Fact]
    public void Match_EmptyOrNullTranscript_ReturnsNull()
    {
        Build().Match(null).ShouldBeNull();
        Build().Match("").ShouldBeNull();
        Build().Match("   ").ShouldBeNull();
    }

    [Fact]
    public void Match_Disabled_ReturnsNullForEveryPhrase()
    {
        Build(enabled: false).Match("sube el volumen local").ShouldBeNull();
    }

    [Fact]
    public void Match_NoPhrasesConfigured_ReturnsNull()
    {
        var empty = new VoiceCommandMatcher(new CommandSettings());
        empty.Match("sube el volumen local").ShouldBeNull();
    }
}