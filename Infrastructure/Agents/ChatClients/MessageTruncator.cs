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

        return messages; // truncation logic added in next task
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
