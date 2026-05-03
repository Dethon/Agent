using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

internal static class MessageTruncator
{
    private const int PerMessageOverhead = 4;
    private const int OtherContentOverhead = 4;

    public static int EstimateTokens(string text)
        => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

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
