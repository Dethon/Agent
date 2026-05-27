namespace McpChannelVoice.Services;

public enum ApprovalResponse { Approved, Declined, Ambiguous }

public static class ApprovalGrammarParser
{
    private static readonly HashSet<string> _affirmative = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "yeah", "yep", "sure", "okay", "ok", "confirm", "confirmed",
        "sí", "si", "vale", "claro", "afirmativo"
    };

    private static readonly HashSet<string> _negative = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "nope", "nah", "cancel", "cancelar", "negativo", "abort", "stop"
    };

    public static ApprovalResponse Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ApprovalResponse.Ambiguous;
        }

        var tokens = text
            .ToLowerInvariant()
            .Split([' ', ',', '.', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries);
        var hasYes = tokens.Any(_affirmative.Contains);
        var hasNo = tokens.Any(_negative.Contains);

        return (hasYes, hasNo) switch
        {
            (true, false) => ApprovalResponse.Approved,
            (false, true) => ApprovalResponse.Declined,
            _ => ApprovalResponse.Ambiguous
        };
    }
}