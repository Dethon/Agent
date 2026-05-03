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

        var groups = BuildDropGroups(messages, pinned);

        var kept = new HashSet<int>(Enumerable.Range(0, messages.Count));
        var currentTokens = tokensBefore;

        foreach (var group in groups)
        {
            if (currentTokens <= threshold) break;

            var groupTokens = group.Sum(idx => EstimateMessageTokens(messages[idx]));
            foreach (var idx in group) kept.Remove(idx);
            currentTokens -= groupTokens;
            droppedCount += group.Count;
        }

        tokensAfter = currentTokens;
        return Enumerable.Range(0, messages.Count)
            .Where(kept.Contains)
            .Select(i => messages[i])
            .ToList();
    }

    private static IReadOnlyList<IReadOnlyList<int>> BuildDropGroups(
        IReadOnlyList<ChatMessage> messages, HashSet<int> pinned)
    {
        var groups = new List<IReadOnlyList<int>>();
        var consumed = new HashSet<int>();

        for (var i = 0; i < messages.Count; i++)
        {
            if (pinned.Contains(i) || consumed.Contains(i)) continue;

            var msg = messages[i];
            var callIds = msg.Contents.OfType<FunctionCallContent>().Select(c => c.CallId).ToHashSet();

            if (msg.Role == ChatRole.Assistant && callIds.Count > 0)
            {
                var group = new List<int> { i };
                consumed.Add(i);
                for (var j = i + 1; j < messages.Count; j++)
                {
                    if (pinned.Contains(j) || consumed.Contains(j)) continue;
                    var hasMatchingResult = messages[j].Contents
                        .OfType<FunctionResultContent>()
                        .Any(r => callIds.Contains(r.CallId));
                    if (hasMatchingResult)
                    {
                        group.Add(j);
                        consumed.Add(j);
                    }
                }
                groups.Add(group);
            }
            else
            {
                groups.Add(new[] { i });
                consumed.Add(i);
            }
        }

        return groups;
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
