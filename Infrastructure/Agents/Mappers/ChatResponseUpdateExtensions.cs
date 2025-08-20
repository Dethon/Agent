using System.Text.Json;
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.Mappers;

public static class ChatResponseUpdateExtensions
{
    public static bool IsFinished(this IEnumerable<ChatResponseUpdate> updates)
    {
        var enumeratedUpdates = updates.ToArray();
        var finishReason = enumeratedUpdates.LastOrDefault()?.FinishReason;
        if (finishReason == null)
        {
            return false;
        }

        var allContents = enumeratedUpdates
            .SelectMany(x => x.Contents)
            .ToArray();
        if (!allContents.Any(x => x is UsageContent))
        {
            return false;
        }

        return finishReason != ChatFinishReason.ToolCalls || allContents.Any(x => x is FunctionCallContent);
    }

    public static AiResponse ToAiResponse(this IEnumerable<ChatResponseUpdate> updates)
    {
        var enumeratedUpdates = updates.ToArray();
        var normalMessage = string.Join("", enumeratedUpdates
            .SelectMany(x => x.Contents)
            .Where(x => x is TextContent)
            .Cast<TextContent>()
            .Select(x => x.Text));

        var toolCallMessageContent = string.Join("\n", enumeratedUpdates
            .SelectMany(x => x.Contents)
            .Where(x => x is FunctionCallContent)
            .Cast<FunctionCallContent>()
            .Select(x => $"{x.Name}(\n{JsonSerializer.Serialize(x.Arguments)}\n)"));

        if (enumeratedUpdates.Any(x => x.FinishReason == ChatFinishReason.ContentFilter))
        {
            normalMessage += "\n\n[Content filtered by LLM.]\n";
        }

        return new AiResponse
        {
            Content = normalMessage,
            ToolCalls = toolCallMessageContent
        };
    }
}