using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

internal static class MessageTruncator
{
    private const int PerMessageOverhead = 4;
    private const int OtherContentOverhead = 4;
    private const double SafetyRatio = 0.95;

    public static int EstimateTokens(string text)
        => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    public static IReadOnlyList<ChatMessage> Truncate(
        IReadOnlyList<ChatMessage> messages,
        int? maxContextTokens,
        out int droppedCount,
        out int tokensBefore,
        out int tokensAfter)
    {
        droppedCount = 0;
        tokensBefore = messages.Sum(EstimateMessageTokens);
        tokensAfter = tokensBefore;

        if (maxContextTokens is null or <= 0 || messages.Count == 0)
        {
            return messages;
        }

        var threshold = (int)Math.Floor(maxContextTokens.Value * SafetyRatio);
        if (tokensBefore <= threshold)
        {
            return messages;
        }

        var lastUserIndex = LastIndexOfRole(messages, ChatRole.User);
        var pinned = new HashSet<int>(
            Enumerable.Range(0, messages.Count)
                .Where(i => messages[i].Role == ChatRole.System || i == lastUserIndex));

        var kept = messages.Select((m, i) => (Message: m, Index: i, Tokens: EstimateMessageTokens(m)))
            .ToList();
        var currentTokens = tokensBefore;

        for (var i = 0; i < kept.Count && currentTokens > threshold; )
        {
            if (pinned.Contains(kept[i].Index))
            {
                i++;
                continue;
            }

            currentTokens -= kept[i].Tokens;
            kept.RemoveAt(i);
            droppedCount++;
            // do not increment i — list shifted left
        }

        tokensAfter = currentTokens;
        return kept.Select(k => k.Message).ToList();
    }

    private static int LastIndexOfRole(IReadOnlyList<ChatMessage> messages, ChatRole role)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == role) return i;
        }
        return -1;
    }

    public static int EstimateMessageTokens(ChatMessage message)
    {
        var contentTokens = message.Contents.Sum(EstimateContentTokens);
        return contentTokens + PerMessageOverhead;
    }

    private static int EstimateContentTokens(AIContent content) => content switch
    {
        TextContent t => EstimateTokens(t.Text),
        TextReasoningContent r => EstimateTokens(r.Text),
        FunctionCallContent fc => EstimateTokens(JsonSerializer.Serialize(
            new { name = fc.Name, arguments = fc.Arguments })),
        FunctionResultContent fr => EstimateTokens(JsonSerializer.Serialize(fr.Result)),
        _ => OtherContentOverhead
    };
}
