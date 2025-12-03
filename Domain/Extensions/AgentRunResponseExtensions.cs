using System.Text.Json;
using Domain.DTOs;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Domain.Extensions;

public static class AgentRunResponseExtensions
{
    public static AiResponse ToAiResponse(this IEnumerable<AgentRunResponseUpdate> updates)
    {
        var contents = updates.SelectMany(x => x.Contents).ToArray();
        var text = string.Join("", contents.OfType<TextContent>().Select(x => x.Text));
        var toolCalls = string.Join("\n", contents.OfType<FunctionCallContent>()
            .Select(x => $"{x.Name}({JsonSerializer.Serialize(x.Arguments)})"));

        return new AiResponse
        {
            Content = text,
            ToolCalls = toolCalls
        };
    }

    public static async IAsyncEnumerable<(AgentRunResponseUpdate, AiResponse?)> ToUpdateAiResponsePairs(
        this IAsyncEnumerable<AgentRunResponseUpdate> updates)
    {
        Dictionary<string, List<AgentRunResponseUpdate>> updatesByMessage = [];
        await foreach (var update in updates)
        {
            if (update.MessageId is not { } messageId)
            {
                yield return (update, null);
                continue;
            }

            if (!updatesByMessage.TryGetValue(messageId, out var messageUpdates))
            {
                messageUpdates = [];
                updatesByMessage[messageId] = messageUpdates;
            }

            messageUpdates.Add(update);

            if (!update.HasUsageOrToolCall())
            {
                yield return (update, null);
                continue;
            }

            yield return (update, messageUpdates.ToAiResponse());
            messageUpdates.Clear();
        }
    }

    private static bool HasUsageOrToolCall(this AgentRunResponseUpdate update)
    {
        return update.Contents.Any(c => c is UsageContent or FunctionCallContent);
    }
}