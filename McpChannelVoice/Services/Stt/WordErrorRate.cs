using System.Text.RegularExpressions;

namespace McpChannelVoice.Services.Stt;

// Word-level error rate (Levenshtein edit distance over normalized word tokens)
// divided by the reference word count. Used to gate segmented STT accuracy
// against the whole-utterance baseline.
public static partial class WordErrorRate
{
    public static double Compute(string reference, string hypothesis)
    {
        var refWords = Normalize(reference);
        var hypWords = Normalize(hypothesis);
        if (refWords.Length == 0)
        {
            return hypWords.Length == 0 ? 0.0 : 1.0;
        }

        var distance = EditDistance(refWords, hypWords);
        return (double)distance / refWords.Length;
    }

    private static string[] Normalize(string text) =>
        Punctuation().Replace(text.ToLowerInvariant(), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int EditDistance(string[] a, string[] b)
    {
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        var curr = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    [GeneratedRegex(@"[^\p{L}\p{Nd}\s]")]
    private static partial Regex Punctuation();
}