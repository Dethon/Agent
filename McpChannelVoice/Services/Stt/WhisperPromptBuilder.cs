using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Stt;

// Builds the initial prompt posted with one transcription. Whisper reads the prompt as text that
// precedes the audio, so the prior segment's transcript goes LAST — closest to what is being
// decoded — and the configured vocabulary first.
//
// whisper.cpp caps the prompt at n_text_ctx/2 (224 tokens) and keeps the TAIL, which would
// silently eat the configured vocabulary on a long continuation. So the cap is applied here
// instead, and it is the prior text that gets trimmed (from its front, at a word boundary):
// operator-authored vocabulary always survives whole. maxChars is a character approximation of
// that token budget, deliberately under it rather than tuned to it.
public static class WhisperPromptBuilder
{
    public static string? Build(
        string? template, string? room, string? locality, string? priorText, int maxChars)
    {
        var configured = Collapse(Substitute(template, room, locality));
        var prior = Collapse(priorText);

        if (configured.Length == 0)
        {
            return prior.Length == 0 ? null : NullIfEmpty(Tail(prior, maxChars));
        }

        var budget = maxChars - configured.Length - 1;
        var tail = prior.Length == 0 || budget <= 0 ? "" : Tail(prior, budget);
        return tail.Length == 0 ? configured : $"{configured} {tail}";
    }

    private static string Substitute(string? template, string? room, string? locality) =>
        (template ?? string.Empty)
            .Replace("{room}", room ?? string.Empty, StringComparison.Ordinal)
            .Replace("{locality}", locality ?? string.Empty, StringComparison.Ordinal);

    // A substituted-away placeholder leaves a double space or a space before a comma; collapsing
    // runs of whitespace is what keeps a satellite with no Locality from reading as a typo.
    private static string Collapse(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Keeps the END of the text (the most recent context) and starts it on a whole word, so a
    // fragment never opens mid-syllable and mis-primes the decoder. A word longer than the whole
    // budget is dropped, not cut: no context primes better than wrong context.
    private static string Tail(string text, int budget)
    {
        if (text.Length <= budget)
        {
            return text;
        }

        if (text[^(budget + 1)] == ' ')
        {
            return text[^budget..];
        }

        var cut = text[^budget..];
        var space = cut.IndexOf(' ');
        return space < 0 ? "" : cut[(space + 1)..];
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    // Load-time check behind ConfigModule's warning: Build posts an over-budget template whole
    // (it never truncates operator vocabulary), so whisper.cpp's tail-keeping truncation would
    // silently eat its front — the exact failure the hub-side cap exists to prevent. Placeholders
    // are unexpanded here, so this measures each template's minimum length.
    public static IReadOnlyList<string> OverBudgetPromptSources(VoiceSettings settings)
    {
        var maxChars = settings.Stt.OpenAi.MaxPromptChars;
        return new[] { (Path: "Stt:OpenAi:Prompt", Template: settings.Stt.OpenAi.Prompt) }
            .Concat(settings.Satellites.Select(kv =>
                (Path: $"Satellites:{kv.Key}:Stt:OpenAi:Prompt", Template: kv.Value.Stt?.OpenAi?.Prompt)))
            .Where(s => Collapse(s.Template).Length > maxChars)
            .Select(s => s.Path)
            .ToList();
    }
}