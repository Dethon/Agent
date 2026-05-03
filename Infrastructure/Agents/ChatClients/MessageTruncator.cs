namespace Infrastructure.Agents.ChatClients;

internal static class MessageTruncator
{
    public static int EstimateTokens(string text)
        => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;
}
