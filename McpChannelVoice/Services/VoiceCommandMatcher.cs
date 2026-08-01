using System.Globalization;
using System.Text;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public enum VoiceCommand
{
    LocalVolumeUp,
    LocalVolumeDown,
    LocalMute,
    LocalUnmute
}

public sealed class VoiceCommandMatcher(CommandSettings settings)
{
    private readonly Dictionary<string, VoiceCommand> _phrases = BuildPhrases(settings);

    private static Dictionary<string, VoiceCommand> BuildPhrases(CommandSettings settings) =>
        settings.Enabled
            ? new[]
                {
                    (settings.Phrases.LocalVolumeUp, VoiceCommand.LocalVolumeUp),
                    (settings.Phrases.LocalVolumeDown, VoiceCommand.LocalVolumeDown),
                    (settings.Phrases.LocalMute, VoiceCommand.LocalMute),
                    (settings.Phrases.LocalUnmute, VoiceCommand.LocalUnmute)
                }
                .SelectMany(entry => entry.Item1.Select(phrase => (Key: Normalize(phrase), entry.Item2)))
                .Where(entry => entry.Key.Length > 0)
                .GroupBy(entry => entry.Key)
                .ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.Ordinal)
            : [];

    // Whole-transcript match only. A command buried in a longer sentence is part of a request the
    // agent has to answer, and swallowing it here would silently drop the rest of what was said.
    public VoiceCommand? Match(string? transcript) =>
        transcript is not null && _phrases.TryGetValue(Normalize(transcript), out var command)
            ? command
            : null;

    // Whisper returns accented, capitalised, punctuated Spanish; the configured phrases are
    // written plain. Both sides run through this so config stays readable and a stray "¿...?"
    // cannot defeat a match.
    private static string Normalize(string text)
    {
        var folded = text
            .Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Where(c => !char.IsPunctuation(c) && !char.IsSymbol(c))
            .Select(char.ToLowerInvariant)
            .ToArray();

        return string.Join(' ', new string(folded).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}