namespace Tests.Integration.Fixtures;

// Helpers for tests that drive a real LLM, where an occasional bad answer says nothing about the
// contract under test. One good answer is enough to prove a contract holds, so a positive
// assertion gets a few attempts. When a parser swallows a wrong-shaped answer, the empty result it
// returns looks exactly like a legitimately empty one, so the warnings it logged carry the reason
// and belong in the failure message.
public static class LlmAttempt
{
    private const int MaxAttempts = 3;

    public static async Task<T> UntilAsync<T>(Func<Task<T>> call, Func<T, bool> usable)
    {
        var result = await call();
        for (var attempt = 1; attempt < MaxAttempts && !usable(result); attempt++)
        {
            result = await call();
        }
        return result;
    }

    public static string Explain(string what, IReadOnlyList<string> warnings) =>
        warnings.Count == 0
            ? $"{what} (no parse warnings logged, so the model answered with a valid but empty response)"
            : $"{what} (parse warnings: {string.Join(" | ", warnings)})";
}