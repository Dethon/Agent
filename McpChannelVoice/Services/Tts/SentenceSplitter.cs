namespace McpChannelVoice.Services.Tts;

// Finds flush points in a partially-received reply so the hub can start speaking while the agent is
// still generating. One sentence per flush is deliberately NOT the unit: every flush is its own TTS
// request, so tiny fragments cost a round trip each and land an audible gap between them. Callers
// pass a minimum length and get the largest complete run available, or nothing.
public static class SentenceSplitter
{
    private static readonly char[] _terminators = ['.', '!', '?', '…'];

    // Tokens that end in '.' without ending a sentence. Deliberately short: a wrong entry only
    // delays a flush to the next boundary, whereas a missing one splits mid-sentence — so the list
    // is biased toward the cheap failure.
    private static readonly string[] _abbreviations =
    [
        "sr", "sra", "srta", "dr", "dra", "ud", "uds", "etc", "ej", "av", "avda", "núm", "pág", "vs"
    ];

    public static bool TryTake(string buffer, int minChars, out string speakable, out string remainder)
    {
        speakable = string.Empty;
        remainder = buffer;

        var boundary = LastBoundary(buffer);
        if (boundary < 0)
        {
            return false;
        }

        var candidate = buffer[..(boundary + 1)].Trim();
        if (candidate.Length < minChars)
        {
            return false;
        }

        speakable = candidate;
        remainder = buffer[(boundary + 1)..].TrimStart();
        return true;
    }

    private static int LastBoundary(string buffer) =>
        Enumerable.Range(0, buffer.Length)
            .Select(offset => buffer.Length - 1 - offset)
            .FirstOrDefault(i => IsBoundary(buffer, i), -1);

    private static bool IsBoundary(string buffer, int i)
    {
        if (!_terminators.Contains(buffer[i]))
        {
            return false;
        }

        // A terminator only ends a sentence at end-of-buffer or before whitespace. That one rule is
        // also what excludes decimals ("1.234,56", "3.5") and the interior dots of an ellipsis: in
        // every case the next character is not whitespace.
        if (i + 1 < buffer.Length && !char.IsWhiteSpace(buffer[i + 1]))
        {
            return false;
        }

        return buffer[i] != '.' || !EndsWithAbbreviation(buffer, i);
    }

    private static bool EndsWithAbbreviation(string buffer, int dot)
    {
        var start = dot;
        while (start > 0 && char.IsLetter(buffer[start - 1]))
        {
            start--;
        }

        var token = buffer[start..dot];
        // A lone letter before the dot is an initial ("J. Crespo"), never a sentence end.
        return token.Length == 1 || _abbreviations.Contains(token.ToLowerInvariant());
    }
}